/* ==========================================================================
   6-ALGORITHM WEBGL GLSL SHADER + THEME PARTICLES ENGINE
   Adapted from user's theme-lab with automatic idle/darkroom suspension
   ========================================================================== */

const THEME_CONFIG = {
  bleu:     { mode: 0.0, particle: "motes" },
  aurora:   { mode: 1.0, particle: "stars" },
  amber:    { mode: 2.0, particle: "embers" },
  sakura:   { mode: 3.0, particle: "petals" },
  obsidian: { mode: 4.0, particle: "none" },
  dune:     { mode: 5.0, particle: "sand" }
};

export function createAtmosphereEngine() {
  let activeMode = 4.0;
  let activeParticle = "none";
  let suspended = false;
  const mouse = { x: 0.5, y: 0.5, sx: 0.5, sy: 0.5 };

  window.addEventListener("pointermove", (e) => {
    mouse.x = e.clientX / Math.max(1, window.innerWidth);
    mouse.y = 1.0 - e.clientY / Math.max(1, window.innerHeight);
  }, { passive: true });

  initShader();
  initParticles();

  function setTheme(themeId) {
    const cfg = THEME_CONFIG[themeId] || THEME_CONFIG.obsidian;
    document.documentElement.dataset.theme = themeId in THEME_CONFIG ? themeId : "obsidian";
    activeMode = cfg.mode;
    activeParticle = cfg.particle;
  }

  function setPureDarkroom(enabled) {
    suspended = Boolean(enabled);
    document.body.classList.toggle("pure-darkroom", suspended);
  }

  function initShader() {
    const canvas = document.getElementById("shaderCanvas");
    if (!canvas) return;
    const gl = canvas.getContext("webgl", {
      alpha: false,
      antialias: false,
      depth: false,
      stencil: false,
      powerPreference: "low-power"
    });
    if (!gl) return;

    const vsSrc = `
      attribute vec2 a_pos;
      void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }
    `;

    const fsSrc = `
      precision mediump float;
      uniform vec2 u_res;
      uniform float u_time;
      uniform vec2 u_mouse;
      uniform float u_mode;

      float hash21(vec2 p) {
        p = fract(p * vec2(127.1, 311.7));
        p += dot(p, p + 34.53);
        return fract(p.x * p.y);
      }

      float noise(vec2 p) {
        vec2 i = floor(p);
        vec2 f = fract(p);
        vec2 u = f * f * (3.0 - 2.0 * f);
        return mix(
          mix(hash21(i), hash21(i + vec2(1.0, 0.0)), u.x),
          mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0, 1.0)), u.x),
          u.y
        );
      }

      float fbm(vec2 p) {
        float v = 0.0, amp = 0.5;
        mat2 rot = mat2(0.80, 0.60, -0.60, 0.80);
        for (int i = 0; i < 4; i++) {
          v += amp * noise(p);
          p = rot * p * 2.02 + vec2(1.7, 9.2);
          amp *= 0.5;
        }
        return v;
      }

      float smin(float a, float b, float k) {
        float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
        return mix(b, a, h) - k * h * (1.0 - h);
      }

      float amberField(vec2 p, vec2 mp, float t) {
        float d = 10.0;
        d = smin(d, length(p - vec2(sin(t*0.42)*0.52, cos(t*0.34)*0.32)) - 0.21, 0.24);
        d = smin(d, length(p - vec2(cos(t*0.37+1.3)*0.64, sin(t*0.45+0.6)*0.38)) - 0.19, 0.24);
        d = smin(d, length(p - vec2(sin(t*0.29+2.8)*0.46, cos(t*0.51+1.9)*0.42)) - 0.23, 0.24);
        d = smin(d, length(p - vec2(cos(t*0.53+4.1)*0.72, sin(t*0.31+3.4)*0.26)) - 0.17, 0.24);
        d = smin(d, length(p - mp) - 0.14, 0.26);
        return d;
      }

      vec2 vortexSwirl(vec2 p, vec2 center, float strength, float radius) {
        vec2 d = p - center;
        float angle = strength * exp(-dot(d, d) / (radius * radius));
        float cs = cos(angle), sn = sin(angle);
        return center + mat2(cs, -sn, sn, cs) * d;
      }

      float mercuryHeight(vec2 p, vec2 mp, float t) {
        float h = 0.0;
        vec2 e0 = vec2(sin(t*0.32)*0.65, cos(t*0.26)*0.38);
        vec2 e1 = vec2(cos(t*0.27+2.1)*0.70, sin(t*0.35+1.2)*0.42);
        vec2 e2 = vec2(sin(t*0.41+4.0)*0.42, -cos(t*0.29)*0.46);
        float d0 = length(p - e0); h += sin(d0*15.0 - t*2.1) / (1.0 + d0*1.6);
        float d1 = length(p - e1); h += sin(d1*13.5 - t*1.85 + 1.2) / (1.0 + d1*1.6);
        float d2 = length(p - e2); h += sin(d2*17.0 - t*2.3 + 2.4) / (1.0 + d2*1.8);
        float dm = length(p - mp);
        h += sin(dm*20.0 - t*3.0) * 0.72 * exp(-dm*2.3);
        return h * 0.22;
      }

      float duneHeight(vec2 p, float t) {
        mat2 r = mat2(0.94, -0.34, 0.34, 0.94);
        vec2 q = r * p;
        vec2 wind = vec2(t*0.045, t*0.012);
        float n1 = 1.0 - abs(noise(q*vec2(1.4, 3.6) + wind)*2.0 - 1.0);
        float n2 = 1.0 - abs(noise(q*vec2(2.8, 7.2) - wind*1.4 + vec2(n1*0.65, 0.0))*2.0 - 1.0);
        return pow(n1, 1.55)*0.65 + pow(n2, 1.9)*0.35;
      }

      void main() {
        vec2 uv = gl_FragCoord.xy / u_res.xy;
        float minR = min(u_res.x, u_res.y);
        vec2 q = (gl_FragCoord.xy - 0.5 * u_res.xy) / minR;
        vec2 mp = (u_mouse * u_res.xy - 0.5 * u_res.xy) / minR;
        float t = u_time;
        vec3 col = vec3(0.0);

        if (u_mode < 0.5) {
          vec2 mOff = (u_mouse - 0.5) * 0.16;
          float w1 = fbm(q*1.45 + mOff + vec2(t*0.05, -t*0.03));
          float w2 = fbm(q*1.75 + vec2(w1*2.1, -w1*1.7) + vec2(-t*0.03, t*0.04));
          col = mix(vec3(0.05, 0.14, 0.28), vec3(0.24, 0.50, 0.76), smoothstep(0.22, 0.78, w2));
          float fil = pow(max(0.0, 1.0 - abs(w2*2.0 - 1.0)), 6.0);
          col = mix(col, vec3(0.76, 0.90, 0.99), fil * 0.5);
        } else if (u_mode < 1.5) {
          vec2 viewTilt = (u_mouse - 0.5) * vec2(0.28, 0.14);
          vec3 acc = vec3(0.0);
          for (int i = 0; i < 10; i++) {
            float fi = float(i) / 9.0;
            vec2 p = (q + viewTilt*(0.4 + fi*0.7)) * (0.85 + fi*1.6);
            float fold = sin(p.x*2.3 + t*0.36 + fi*2.1)*0.24 + sin(p.x*4.7 - t*0.48 + fi*3.8)*0.10;
            float rays = 0.55 + 0.45*sin(p.x*15.0 + fold*9.0 - t*1.1 + fi*5.0);
            float cur = exp(-abs(p.y - fold - 0.08 + fi*0.18)*7.0) * rays * (1.0 - fi*0.52);
            vec3 c1 = mix(vec3(0.22, 0.92, 0.72), vec3(0.56, 0.44, 0.96), smoothstep(0.15, 0.7, fi + uv.y*0.25));
            acc += c1 * cur * 0.18;
          }
          col = acc;
        } else if (u_mode < 2.5) {
          float eps = 0.018;
          float dC = amberField(q, mp, t);
          float dx = amberField(q + vec2(eps, 0.0), mp, t) - dC;
          float dy = amberField(q + vec2(0.0, eps), mp, t) - dC;
          vec3 N = normalize(vec3(dx, dy, 0.055));
          vec3 L = normalize(vec3(0.55, 0.75, 0.65));
          float diff = max(0.0, dot(N, L));
          float spec = pow(max(0.0, dot(reflect(-L, N), vec3(0.0, 0.0, 1.0))), 18.0);
          float body = smoothstep(0.06, -0.05, dC);
          vec3 dropCol = mix(vec3(0.42, 0.14, 0.04), vec3(0.96, 0.64, 0.24), diff*0.85 + 0.15) + vec3(1.0, 0.92, 0.74)*spec*0.75;
          col = vec3(0.96, 0.64, 0.24)*exp(-max(0.0, dC)*4.8)*0.38 + dropCol*body*0.9;
        } else if (u_mode < 3.5) {
          vec2 p = q * 1.15;
          p = vortexSwirl(p, vec2(sin(t*0.26)*0.48, cos(t*0.21)*0.34), 2.1, 0.55);
          p = vortexSwirl(p, vec2(-cos(t*0.23)*0.52, sin(t*0.29)*0.32), -2.3, 0.52);
          p = vortexSwirl(p, mp*1.15, 2.2, 0.36);
          float rings = sin(length(p - vec2(-0.18, 0.08))*20.0 - t*0.4)*0.55 + sin(length(p - vec2(0.24, -0.14))*16.0 + t*0.32)*0.45;
          col = mix(vec3(0.992, 0.972, 0.964), vec3(0.945, 0.820, 0.845), smoothstep(0.15, 0.75, rings)*0.6);
          col = mix(col, vec3(0.84, 0.54, 0.62), smoothstep(0.72, 0.96, rings)*0.32);
        } else if (u_mode < 4.5) {
          float eps = 0.008;
          float hC = mercuryHeight(q, mp, t);
          float hX = mercuryHeight(q + vec2(eps, 0.0), mp, t);
          float hY = mercuryHeight(q + vec2(0.0, eps), mp, t);
          vec3 N = normalize(vec3((hC - hX)*5.0, (hC - hY)*5.0, 0.14));
          vec3 V = vec3(0.0, 0.0, 1.0);
          vec3 H1 = normalize(normalize(vec3(-0.45, 0.68, 0.72)) + V);
          float spec = pow(max(0.0, dot(N, H1)), 32.0);
          float fres = pow(1.0 - max(0.0, dot(N, V)), 3.0);
          col = mix(vec3(0.04, 0.055, 0.085), vec3(0.22, 0.28, 0.38), smoothstep(-0.35, 0.38, hC)*0.65);
          col += vec3(0.86, 0.92, 1.0)*spec*0.95 + vec3(0.42, 0.68, 0.96)*fres*0.42;
        } else {
          float eps = 0.010;
          float hC = duneHeight(q, t);
          float hX = duneHeight(q + vec2(eps, 0.0), t);
          float hY = duneHeight(q + vec2(0.0, eps), t);
          vec3 N = normalize(vec3((hC - hX)*7.0, (hC - hY)*7.0, 0.22));
          vec3 sun = normalize(vec3(0.72 + (u_mouse.x - 0.5)*0.4, 0.42 + (u_mouse.y - 0.5)*0.25, 0.38));
          float lit = clamp(dot(N, sun), 0.0, 1.0);
          col = mix(vec3(0.11, 0.22, 0.34), vec3(0.56, 0.33, 0.20), smoothstep(0.08, 0.55, lit));
          col = mix(col, vec3(0.88, 0.70, 0.47), smoothstep(0.48, 0.88, lit));
          col = mix(col, vec3(1.0, 0.87, 0.54), pow(lit, 5.0)*smoothstep(0.52, 0.92, hC)*0.65);
        }

        gl_FragColor = vec4(col, 1.0);
      }
    `;

    function compile(type, src) {
      const sh = gl.createShader(type);
      gl.shaderSource(sh, src);
      gl.compileShader(sh);
      return sh;
    }

    const prog = gl.createProgram();
    gl.attachShader(prog, compile(gl.VERTEX_SHADER, vsSrc));
    gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, fsSrc));
    gl.linkProgram(prog);
    gl.useProgram(prog);

    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, "a_pos");
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    const uRes = gl.getUniformLocation(prog, "u_res");
    const uTime = gl.getUniformLocation(prog, "u_time");
    const uMouse = gl.getUniformLocation(prog, "u_mouse");
    const uMode = gl.getUniformLocation(prog, "u_mode");

    function resize() {
      canvas.width = Math.max(320, Math.floor(window.innerWidth * 0.42));
      canvas.height = Math.max(200, Math.floor(window.innerHeight * 0.42));
      gl.viewport(0, 0, canvas.width, canvas.height);
    }
    window.addEventListener("resize", resize, { passive: true });
    resize();

    let lastTs = 0, elapsed = 0;
    function loop(ts) {
      requestAnimationFrame(loop);
      if (suspended || document.hidden || ts - lastTs < 34) return;
      const dt = lastTs ? Math.min(0.1, (ts - lastTs) / 1000) : 0.033;
      lastTs = ts;
      elapsed += dt;
      mouse.sx += (mouse.x - mouse.sx) * 0.08;
      mouse.sy += (mouse.y - mouse.sy) * 0.08;

      gl.uniform2f(uRes, canvas.width, canvas.height);
      gl.uniform1f(uTime, elapsed);
      gl.uniform2f(uMouse, mouse.sx, mouse.sy);
      gl.uniform1f(uMode, activeMode);
      gl.drawArrays(gl.TRIANGLES, 0, 6);
    }
    requestAnimationFrame(loop);
  }

  function initParticles() {
    const c = document.getElementById("ambientCanvas");
    if (!c) return;
    const ctx = c.getContext("2d");
    let W = 0, H = 0;
    const items = Array.from({ length: 36 }, () => ({
      x: Math.random(),
      y: Math.random(),
      r: 1 + Math.random() * 2.2,
      sp: 0.25 + Math.random() * 0.75,
      ph: Math.random() * Math.PI * 2,
      rot: Math.random() * Math.PI * 2
    }));

    function resize() {
      W = c.width = window.innerWidth;
      H = c.height = window.innerHeight;
    }
    window.addEventListener("resize", resize, { passive: true });
    resize();

    let last = 0;
    function draw(ts) {
      requestAnimationFrame(draw);
      if (suspended || document.hidden || ts - last < 34) return;
      const dt = last ? Math.min(0.05, (ts - last) / 1000) : 0.033;
      last = ts;
      ctx.clearRect(0, 0, W, H);
      if (activeParticle === "none") return;

      for (let i = 0; i < items.length; i++) {
        const p = items[i];
        p.ph += dt * (1.2 + p.sp);
        if (activeParticle === "stars") {
          const tw = 0.25 + 0.75 * Math.pow(0.5 + 0.5 * Math.sin(p.ph * 1.4), 2.0);
          ctx.fillStyle = i % 4 === 0 ? `rgba(148,245,224,${(tw * 0.55).toFixed(2)})` : `rgba(235,226,255,${(tw * 0.48).toFixed(2)})`;
          ctx.beginPath();
          ctx.arc(p.x * W, p.y * H * 0.78, p.r * 0.65, 0, Math.PI * 2);
          ctx.fill();
        } else if (activeParticle === "petals") {
          if (i > 18) continue;
          p.y += dt * 0.02 * p.sp;
          p.x += Math.sin(p.ph * 0.8) * dt * 0.014 + dt * 0.005;
          p.rot += dt * (0.6 + p.sp * 0.5);
          if (p.y > 1.05) { p.y = -0.05; p.x = Math.random(); }
          if (p.x > 1.05) p.x = -0.05;
          ctx.save();
          ctx.translate(p.x * W, p.y * H);
          ctx.rotate(p.rot);
          ctx.scale(1, 0.55 + 0.35 * Math.sin(p.ph));
          ctx.fillStyle = "rgba(227, 138, 155, 0.36)";
          ctx.beginPath();
          ctx.ellipse(0, 0, 5 + p.r, 3 + p.r * 0.4, 0, 0, Math.PI * 2);
          ctx.fill();
          ctx.restore();
        } else if (activeParticle === "embers") {
          if (i > 24) continue;
          p.y -= dt * 0.022 * p.sp;
          p.x += Math.sin(p.ph) * dt * 0.007;
          if (p.y < -0.04) { p.y = 1.04; p.x = Math.random(); }
          const glow = 0.25 + 0.55 * (0.5 + 0.5 * Math.sin(p.ph * 1.8));
          ctx.fillStyle = `rgba(255, 206, 126, ${(glow * 0.48).toFixed(2)})`;
          ctx.beginPath();
          ctx.arc(p.x * W, p.y * H, p.r * 0.8, 0, Math.PI * 2);
          ctx.fill();
        } else if (activeParticle === "sand") {
          p.x += dt * 0.038 * p.sp;
          p.y += Math.sin(p.ph * 0.7) * dt * 0.005;
          if (p.x > 1.04) { p.x = -0.04; p.y = Math.random(); }
          ctx.fillStyle = `rgba(246, 208, 124, ${(0.16 + 0.2 * Math.sin(p.ph)).toFixed(2)})`;
          ctx.fillRect(p.x * W, p.y * H, p.r * 2.2, 1.1);
        } else {
          if (i > 22) continue;
          p.x += dt * 0.007 * p.sp;
          p.y += Math.sin(p.ph * 0.6) * dt * 0.004;
          if (p.x > 1.04) p.x = -0.04;
          ctx.fillStyle = `rgba(186, 228, 252, ${(0.14 + 0.2 * (0.5 + 0.5 * Math.sin(p.ph))).toFixed(2)})`;
          ctx.beginPath();
          ctx.arc(p.x * W, p.y * H, p.r * 0.68, 0, Math.PI * 2);
          ctx.fill();
        }
      }
    }
    requestAnimationFrame(draw);
  }

  return { setTheme, setPureDarkroom };
}

/** Underdamped spring hover effect (0 -> lift -> settle) for visible cards */
export function attachSpringCard(card) {
  if (card._springAttached) return;
  card._springAttached = true;

  const surface = card.querySelector(".card-surface");
  const sheen = card.querySelector(".card-sheen");
  if (!surface) return;

  const s = { lift: 0, vLift: 0, tLift: 0, rx: 0, vRx: 0, tRx: 0, ry: 0, vRy: 0, tRy: 0, sc: 1, vSc: 0, tSc: 1 };
  let raf = 0, lastT = 0, hovered = false;

  const step = (p, v, tgt, k, c, dt) => {
    const nv = (v + (tgt - p) * k * dt) * Math.exp(-c * dt);
    return [p + nv * dt, nv];
  };

  function tick(now) {
    const dt = Math.min(0.032, Math.max(0.008, (now - lastT) / 1000));
    lastT = now;
    const k = hovered ? 210 : 140, c = hovered ? 12.2 : 16.5;

    [s.lift, s.vLift] = step(s.lift, s.vLift, s.tLift, k, c, dt);
    [s.rx, s.vRx] = step(s.rx, s.vRx, s.tRx, 180, 14, dt);
    [s.ry, s.vRy] = step(s.ry, s.vRy, s.tRy, 180, 14, dt);
    [s.sc, s.vSc] = step(s.sc, s.vSc, s.tSc, k, c, dt);

    surface.style.transform =
      `perspective(950px) translate3d(0, ${s.lift.toFixed(2)}px, 0) ` +
      `rotateX(${s.rx.toFixed(2)}deg) rotateY(${s.ry.toFixed(2)}deg) scale(${s.sc.toFixed(4)})`;

    if (sheen) {
      sheen.style.setProperty("--gx", `${(50 + s.ry * 4.5).toFixed(1)}%`);
      sheen.style.setProperty("--gy", `${(30 - s.rx * 4.5).toFixed(1)}%`);
    }

    const energy = Math.abs(s.lift - s.tLift) + Math.abs(s.vLift) * 0.03 + Math.abs(s.rx - s.tRx) + Math.abs(s.ry - s.tRy);
    if (energy > 0.05 || hovered) {
      raf = requestAnimationFrame(tick);
    } else {
      raf = 0;
      surface.style.transform = "";
    }
  }

  const start = () => { if (!raf) { lastT = performance.now(); raf = requestAnimationFrame(tick); } };

  card.addEventListener("pointerenter", () => {
    hovered = true;
    s.tLift = -6; s.vLift = -110;
    s.tSc = 1.016; s.vSc = 0.14;
    start();
  });

  card.addEventListener("pointermove", (e) => {
    if (!hovered) return;
    const r = card.getBoundingClientRect();
    const nx = (e.clientX - r.left) / Math.max(1, r.width) - 0.5;
    const ny = (e.clientY - r.top) / Math.max(1, r.height) - 0.5;
    s.tRy = nx * 9;
    s.tRx = -ny * 7;
    start();
  });

  card.addEventListener("pointerleave", () => {
    hovered = false;
    s.tLift = s.tRx = s.tRy = 0;
    s.tSc = 1;
    start();
  });
}
