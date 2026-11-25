import { icon } from "./icons.js";
import { THEMES, setLanguage, getLanguage, t } from "./i18n.js";
import { createAtmosphereEngine, attachSpringCard } from "./atmosphere.js";
import { rpc, onHostEvent, getIsDesktop, getThumbUrl, getPreviewUrl, sendDropFiles } from "./bridge.js";
import { SpatialAtlas } from "./atlas.js";

const $ = (id) => document.getElementById(id);

const state = {
  photos: [],
  filtered: [],
  selectedIds: new Set(),
  focusedId: null,
  lastClickedIndex: -1,
  filterMode: "all",
  brandFilter: "",
  searchText: "",
  sortMode: "dateDesc",
  viewMode: "grid",
  writeMode: "direct",
  thumbSize: 230,
  fitContain: false,
  pureDarkroom: false,
  settings: {},
  undo: { canUndo: false, description: "" },
  parsedGps: null,
  copiedMeta: null,
  loupeIndex: -1,
  loupeZoom: 1,
  loupePanX: 0,
  loupePanY: 0,
  loupeMatte: false,
  diagItems: [],
  gpxProposals: []
};

const atmosphere = createAtmosphereEngine();
const atlas = new SpatialAtlas("atlasMap", {
  onPointPicked: ({ lat, lon, label }) => {
    const dms = formatDms(lat, lon);
    $("atlasCoordReadout").textContent = `${lat.toFixed(6)}, ${lon.toFixed(6)} · ${dms}`;
    $("atlasPlaceReadout").textContent = label || "WGS-84";
  },
  onPhotoPinClick: (photoId) => {
    selectSingle(photoId);
    renderAll();
  }
});

/* ==========================================================================
   INITIALIZATION & HOST EVENTS
   ========================================================================== */
async function bootstrap() {
  injectStaticIcons();
  buildThemeDots();
  bindEvents();

  const initData = await rpc("init");
  state.settings = initData.settings || {};
  state.photos = initData.photos || [];
  state.undo = initData.undo || { canUndo: false };
  state.exifToolReady = Boolean(initData.exifToolReady);

  setLanguage(state.settings.lastLanguage || "zh");
  state.writeMode = state.settings.lastWriteMode === 0 ? "copy" : "direct";
  state.viewMode = state.settings.viewMode || "grid";
  state.thumbSize = state.settings.thumbSize || 230;
  state.pureDarkroom = Boolean(state.settings.pureDarkroomMode);

  if (getIsDesktop()) $("winControls").hidden = false;
  $("thumbSizeRange").value = state.thumbSize;
  $("jpegQualityRange").value = state.settings.jpegQuality || 92;
  $("jpegQualityVal").textContent = `${$("jpegQualityRange").value}%`;
  $("daemonBadge").classList.toggle("off", !initData.exifToolReady);
  $("daemonBadgeText").textContent = initData.exifToolReady ? t("daemonReady") : t("daemonOff");

  applyTheme(state.settings.theme || "obsidian", false);
  atmosphere.setPureDarkroom(state.pureDarkroom);
  $("btnPureDarkroom").classList.toggle("active", state.pureDarkroom);

  if (state.photos.length > 0 && !state.focusedId) {
    state.focusedId = state.photos[0].id;
    state.selectedIds.add(state.photos[0].id);
  }

  applyLanguage();
  setViewMode(state.viewMode, false);
  setWriteMode(state.writeMode, false);
  renderAll();
}

onHostEvent("photosUpdated", (data) => {
  state.photos = (data.photos || []).map((p) => ({ ...p, _rev: Date.now() }));
  if (data.undo) state.undo = data.undo;
  if (data.recentLocations) state.settings.recentLocations = data.recentLocations;
  // Reconcile selection & focus
  const valid = new Set(state.photos.map((p) => p.id));
  for (const id of [...state.selectedIds]) if (!valid.has(id)) state.selectedIds.delete(id);
  if (!state.focusedId || !valid.has(state.focusedId)) state.focusedId = state.photos[0]?.id || null;
  renderAll();
});

onHostEvent("progress", (p) => {
  const wrap = $("progressWrap");
  const fill = $("progressFill");
  if (p.busy) {
    wrap.hidden = false;
    $("statusMessage").textContent = p.message || "Working...";
    if (typeof p.percent === "number") {
      fill.classList.remove("indeterminate");
      fill.style.width = `${Math.max(0, Math.min(100, p.percent))}%`;
    } else {
      fill.classList.add("indeterminate");
    }
  } else {
    wrap.hidden = true;
    fill.classList.remove("indeterminate");
    if (p.message) $("statusMessage").textContent = p.message;
  }
});

onHostEvent("toast", (d) => showToast(d.message, d.kind));
onHostEvent("windowState", () => {});

/* ==========================================================================
   RENDER PIPELINE & VIRTUALIZED GRID
   ========================================================================== */
function renderAll() {
  computeFiltered();
  renderSidebar();
  renderToolbar();
  renderMainStage();
  renderInspector();
  renderStatusBar();
  renderUndoButton();
  if (state.viewMode === "atlas") {
    atlas.syncWorkspacePhotos(state.photos, state.focusedId);
  }
}

function computeFiltered() {
  const q = state.searchText.trim().toLowerCase();
  const list = state.photos.filter((p) => {
    if (state.filterMode === "noGps" && p.hasGps) return false;
    if (state.filterMode === "hasGps" && !p.hasGps) return false;
    if (state.filterMode === "mismatch" && !p.isExtensionMismatched) return false;
    if (state.filterMode === "dateIssue" && p.dateTaken && !String(p.dateTaken).startsWith("0000")) return false;
    if (state.filterMode === "video" && !p.isVideo) return false;
    if (state.brandFilter && (p.cameraMake || "").toUpperCase() !== state.brandFilter) return false;
    if (q) {
      const hay = `${p.fileName} ${p.cameraDisplay} ${p.lensModel || ""} ${p.dateTaken || ""} ${p.format} ${p.path}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });

  list.sort((a, b) => {
    switch (state.sortMode) {
      case "dateAsc": return (a.dateTaken || "9999").localeCompare(b.dateTaken || "9999");
      case "nameAsc": return a.fileName.localeCompare(b.fileName);
      case "camera": return (a.cameraDisplay || "zzz").localeCompare(b.cameraDisplay || "zzz");
      default: return (b.dateTaken || "").localeCompare(a.dateTaken || "");
    }
  });
  state.filtered = list;
}

function renderSidebar() {
  const all = state.photos;
  const counts = {
    all: all.length,
    noGps: all.filter((p) => !p.hasGps).length,
    hasGps: all.filter((p) => p.hasGps).length,
    dateIssue: all.filter((p) => !p.dateTaken || String(p.dateTaken).startsWith("0000")).length,
    mismatch: all.filter((p) => p.isExtensionMismatched).length,
    video: all.filter((p) => p.isVideo).length
  };

  const items = [
    { id: "all", icon: "grid", label: t("filterAll"), count: counts.all },
    { id: "noGps", icon: "pin", label: t("filterNoGps"), count: counts.noGps },
    { id: "hasGps", icon: "compass", label: t("filterHasGps"), count: counts.hasGps },
    { id: "dateIssue", icon: "clock", label: t("filterDateIssue"), count: counts.dateIssue },
    { id: "mismatch", icon: "alert", label: t("filterMismatch"), count: counts.mismatch },
    { id: "video", icon: "video", label: t("filterVideo"), count: counts.video }
  ];

  $("filterList").innerHTML = items.map((it) => `
    <button class="sb-item ${state.filterMode === it.id && !state.brandFilter ? "active" : ""}" data-filter="${it.id}">
      ${icon(it.icon)}
      <span class="sb-item-label">${it.label}</span>
      <span class="sb-count mono">${it.count}</span>
    </button>
  `).join("");

  // Dynamic camera brands
  const brandMap = new Map();
  for (const p of all) {
    const mk = (p.cameraMake || "").trim().toUpperCase();
    if (mk) brandMap.set(mk, (brandMap.get(mk) || 0) + 1);
  }
  const brands = [...brandMap.entries()].sort((a, b) => b[1] - a[1]).slice(0, 8);
  $("brandGroup").hidden = brands.length === 0;
  $("brandList").innerHTML = brands.map(([mk, cnt]) => `
    <button class="sb-item ${state.brandFilter === mk ? "active" : ""}" data-brand="${esc(mk)}">
      ${icon("camera")}
      <span class="sb-item-label">${esc(mk)}</span>
      <span class="sb-count mono">${cnt}</span>
    </button>
  `).join("");

  // Saved pins
  const pins = state.settings.savedLocations || [];
  $("savedPinsList").innerHTML = pins.length === 0
    ? `<div style="padding:4px 8px;font-size:11px;color:var(--text-muted);">${t("noSavedPins")}</div>`
    : pins.map((pin) => `
      <div class="sb-pin-row" data-pin-id="${esc(pin.id)}">
        ${icon("star")}
        <span class="sb-pin-name" title="${pin.latitude}, ${pin.longitude}">${esc(pin.name)}</span>
        <button class="sb-pin-del" data-del-pin="${esc(pin.id)}" title="Remove">${icon("close")}</button>
      </div>
    `).join("");
}

function renderToolbar() {
  const hasAny = state.photos.length > 0;
  $("emptyStage").hidden = hasAny || state.viewMode === "atlas";
  $("gridControls").hidden = state.viewMode !== "grid";
}

function renderMainStage() {
  $("gridView").hidden = state.viewMode !== "grid" || state.photos.length === 0;
  $("tableView").hidden = state.viewMode !== "table" || state.photos.length === 0;
  $("atlasView").hidden = state.viewMode !== "atlas";

  if (state.viewMode === "grid" && state.photos.length > 0) renderVirtualGrid();
  else if (state.viewMode === "table" && state.photos.length > 0) renderTable();
}

function renderVirtualGrid() {
  const container = $("gridView");
  const w = Math.max(320, container.clientWidth - 28);
  const gap = 14;
  const cols = Math.max(1, Math.floor((w + gap) / (state.thumbSize + gap)));
  const cardW = (w - gap * (cols - 1)) / cols;
  const cardH = Math.round(cardW * 0.75 + 72);
  const rowH = cardH + gap;
  const totalRows = Math.ceil(state.filtered.length / cols);

  $("gridSpacer").style.height = `${Math.max(totalRows * rowH, 100)}px`;
  const gridWin = $("gridWindow");
  gridWin.style.setProperty("--cols", cols);

  const scrollTop = container.scrollTop;
  const viewH = container.clientHeight || 600;
  const startRow = Math.max(0, Math.floor(scrollTop / rowH) - 1);
  const endRow = Math.min(totalRows - 1, Math.ceil((scrollTop + viewH) / rowH) + 1);

  gridWin.style.top = `${startRow * rowH}px`;
  const startIdx = startRow * cols;
  const endIdx = Math.min(state.filtered.length, (endRow + 1) * cols);

  const frag = document.createDocumentFragment();
  for (let i = startIdx; i < endIdx; i++) {
    const p = state.filtered[i];
    const isSel = state.selectedIds.has(p.id);
    const isFoc = state.focusedId === p.id;
    const card = document.createElement("article");
    card.className = `photo-card ${isSel ? "is-selected" : ""} ${isFoc ? "is-focused" : ""}`;
    card.dataset.id = p.id;
    card.dataset.idx = i;
    card.draggable = true;

    const exp = [p.focalLength, p.aperture, p.shutterSpeed, p.iso ? `ISO${p.iso}` : null].filter(Boolean).join(" · ");
    const pendingGps = isSel && state.parsedGps ? `${state.parsedGps.lat.toFixed(4)}, ${state.parsedGps.lon.toFixed(4)}` : "";

    card.innerHTML = `
      <div class="card-surface">
        <div class="card-sheen"></div>
        <div class="card-thumb-wrap">
          <img src="${getThumbUrl(p)}" loading="lazy" alt="" />
          <button class="card-check" data-check="${p.id}" title="Select">${isSel ? icon("check") : ""}</button>
          <div class="card-badges-tr">
            ${p.isExtensionMismatched ? `<span class="pill-tag warn" title="${esc(p.wrongExtensionHint)}">${esc(p.format)} !</span>` : `<span class="pill-tag mono">${esc(p.format)}</span>`}
          </div>
          <div class="card-bottom-overlay">
            <span class="card-gps-chip mono">
              ${pendingGps ? `<span class="pill-tag pending">→ ${pendingGps}</span>` : (p.hasGps ? `<span class="pill-tag gps">GPS</span>` : `<span class="pill-tag nogps">NO GPS</span>`)}
            </span>
            ${p.solar ? `<span class="solar-dot solar-${p.solar.phase}" title="${t("solar" + p.solar.phase)} · ${p.solar.elevationDegrees}°"></span>` : ""}
          </div>
        </div>
        <div class="card-meta">
          <div class="card-name" title="${esc(p.fileName)}">${esc(p.fileName)}</div>
          <div class="card-sub">
            <span>${esc(p.cameraDisplay || "—")}</span>
            <span class="mono">${esc(p.dateTakenDate ? `${p.dateTakenDate} ${p.dateTakenTime || ""}` : t("noDate"))}</span>
          </div>
          <div class="card-exposure mono">${esc(exp || p.status)}</div>
        </div>
      </div>
    `;
    attachSpringCard(card);
    frag.appendChild(card);
  }
  gridWin.replaceChildren(frag);
}

function renderTable() {
  const rows = state.filtered.slice(0, 600).map((p, idx) => {
    const isSel = state.selectedIds.has(p.id);
    const isFoc = state.focusedId === p.id;
    const exp = [p.focalLength, p.aperture, p.shutterSpeed, p.iso ? `ISO${p.iso}` : null].filter(Boolean).join(" ");
    const pendingGps = isSel && state.parsedGps ? ` → ${state.parsedGps.lat.toFixed(4)}, ${state.parsedGps.lon.toFixed(4)}` : "";

    return `
      <tr class="${isSel ? "is-selected" : ""} ${isFoc ? "is-focused" : ""}" data-id="${p.id}" data-idx="${idx}">
        <td><input type="checkbox" data-check="${p.id}" ${isSel ? "checked" : ""} /></td>
        <td><img class="tbl-thumb" src="${getThumbUrl(p)}" loading="lazy" alt="" /></td>
        <td title="${esc(p.path)}" style="max-width:136px;overflow:hidden;text-overflow:ellipsis;"><strong>${esc(p.fileName)}</strong></td>
        <td class="mono">${esc(p.dateTaken || "—")}</td>
        <td style="max-width:110px;overflow:hidden;text-overflow:ellipsis;">${esc(p.cameraDisplay || "—")}</td>
        <td class="mono" style="color:var(--text-secondary);max-width:116px;overflow:hidden;text-overflow:ellipsis;">${esc(exp || "—")}</td>
        <td class="mono" style="max-width:136px;overflow:hidden;text-overflow:ellipsis;">${esc(p.gpsDisplay)}${pendingGps ? `<span style="color:var(--warn);font-weight:600;">${pendingGps}</span>` : ""}</td>
        <td>${p.solar ? `<span class="solar-dot solar-${p.solar.phase}" style="display:inline-block;margin-right:4px;"></span>${p.solar.elevationDegrees}°` : "—"}</td>
        <td>${p.isExtensionMismatched ? `<span class="pill-tag warn">${esc(p.format)} !</span>` : `<span class="mono">${esc(p.format)}</span>`}</td>
        <td style="color:var(--text-muted);">${esc(p.status)}</td>
      </tr>
    `;
  }).join("");
  $("tableBody").innerHTML = rows;
}

function renderInspector() {
  const photo = state.photos.find((p) => p.id === state.focusedId) || null;
  const selCount = state.selectedIds.size;

  if (!photo) {
    $("inspTitle").textContent = t("noSelection");
    $("inspSub").textContent = "";
    $("inspGpsLine").textContent = "";
    $("inspThumbImg").hidden = true;
    $("inspPills").innerHTML = "";
    $("inspSolarStrip").hidden = true;
  } else {
    $("inspTitle").textContent = photo.fileName;
    $("inspSub").textContent = [photo.cameraDisplay, photo.lensModel].filter(Boolean).join(" · ") || photo.format;
    $("inspGpsLine").textContent = photo.hasGps ? `GPS: ${photo.gpsDisplay}` : "GPS: —";
    $("inspThumbImg").src = getThumbUrl(photo);
    $("inspThumbImg").hidden = false;

    const pills = [
      photo.focalLength,
      photo.aperture,
      photo.shutterSpeed,
      photo.iso ? `ISO ${photo.iso}` : null,
      photo.imageWidth && photo.imageHeight ? `${photo.imageWidth}×${photo.imageHeight}` : null,
      photo.dateTaken
    ].filter(Boolean);
    $("inspPills").innerHTML = pills.map((v) => `<span class="t-pill mono">${esc(v)}</span>`).join("");

    if (photo.solar) {
      $("inspSolarStrip").hidden = false;
      $("inspSolarDot").className = `solar-dot solar-${photo.solar.phase}`;
      $("inspSolarPhase").textContent = t("solar" + photo.solar.phase);
      $("inspSolarData").textContent = `${photo.solar.elevationDegrees}° · ${photo.solar.azimuthDegrees}° ${photo.solar.compassDirection}`;
    } else {
      $("inspSolarStrip").hidden = true;
    }
  }

  // Update action buttons with dynamic selected count
  $("btnWriteGps").textContent = t("writeGpsBtn", selCount);
  $("btnWriteGps").disabled = selCount === 0 || !state.parsedGps;
  $("btnAtlasApplySel").textContent = t("atlasApplySelected", selCount);
  $("btnAtlasApplySel").disabled = selCount === 0 || !atlas.pickedWgs;
  $("btnWriteExactDate").textContent = t("writeDateBtn", selCount);
  $("btnApplyShift").textContent = t("applyShiftBtn", selCount);
  $("btnWriteSeqDate").textContent = t("applySeqBtn", selCount);
  $("btnConvertFormat").textContent = t("convertBtn", selCount);
  $("btnWriteCameraInfo").textContent = t("writeCameraBtn", selCount);
  $("btnPasteMeta").disabled = !state.copiedMeta || selCount === 0;

  // Extension mismatch audit
  const mismatched = state.photos.filter((p) => p.isExtensionMismatched);
  if (mismatched.length === 0) {
    $("mismatchSummaryBox").className = "preview-box ok";
    $("mismatchSummaryBox").textContent = t("noMismatch");
    $("btnFixExtensions").disabled = true;
  } else {
    $("mismatchSummaryBox").className = "preview-box err";
    $("mismatchSummaryBox").textContent = getLanguage() === "en"
      ? `${mismatched.length} extension mismatch(es) detected`
      : `检测到 ${mismatched.length} 个后缀异常文件`;
    $("btnFixExtensions").disabled = false;
  }

  updateShiftPreview();
}

function renderStatusBar() {
  const total = state.photos.length;
  const sel = state.selectedIds.size;
  const gps = state.photos.filter((p) => p.hasGps).length;
  $("statsSummary").textContent = t("statsBar", total, sel, gps);
}

function renderUndoButton() {
  const btn = $("btnUndo");
  btn.disabled = !state.undo.canUndo;
  btn.innerHTML = `${icon("undo")} <span>${t("undo")}</span>`;
  btn.title = state.undo.description ? `${t("undo")}: ${state.undo.description} (Ctrl+Z)` : "Ctrl+Z";
}

/* ==========================================================================
   EVENT BINDINGS
   ========================================================================== */
function bindEvents() {
  $("btnToggleSidebar").addEventListener("click", () => document.body.classList.toggle("sidebar-collapsed"));

  // Window drag & caption controls
  $("dragRegion").addEventListener("mousedown", (e) => {
    if (e.button === 0 && getIsDesktop()) rpc("windowControl", { action: "drag" });
  });
  $("dragRegion").addEventListener("dblclick", () => {
    if (getIsDesktop()) rpc("windowControl", { action: "maximize" });
  });
  $("btnWinMin").addEventListener("click", () => rpc("windowControl", { action: "minimize" }));
  $("btnWinMax").addEventListener("click", () => rpc("windowControl", { action: "maximize" }));
  $("btnWinClose").addEventListener("click", () => rpc("windowControl", { action: "close" }));

  // View & WriteMode toggles
  $("viewSeg").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-view]");
    if (btn) setViewMode(btn.dataset.view, true);
  });
  $("writeModeSeg").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-wmode]");
    if (btn) setWriteMode(btn.dataset.wmode, true);
  });

  $("themeDots").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-theme]");
    if (btn) applyTheme(btn.dataset.theme, true);
  });

  $("btnPureDarkroom").addEventListener("click", () => {
    state.pureDarkroom = !state.pureDarkroom;
    atmosphere.setPureDarkroom(state.pureDarkroom);
    $("btnPureDarkroom").classList.toggle("active", state.pureDarkroom);
    rpc("saveSettings", { pureDarkroomMode: state.pureDarkroom });
  });

  $("btnLang").addEventListener("click", () => {
    const next = getLanguage() === "zh" ? "en" : "zh";
    setLanguage(next);
    state.settings.lastLanguage = next;
    rpc("saveSettings", { lastLanguage: next });
    applyLanguage();
    renderAll();
  });

  $("btnUndo").addEventListener("click", runUndo);

  // Import
  $("btnAddFiles").addEventListener("click", () => rpc("addFiles"));
  $("btnEmptyFiles").addEventListener("click", () => rpc("addFiles"));
  $("btnAddFolder").addEventListener("click", () => rpc("addFolder", { includeSubfolders: $("chkSubfolders").checked }));
  $("btnEmptyFolder").addEventListener("click", () => rpc("addFolder", { includeSubfolders: $("chkSubfolders").checked }));

  // Sidebar Filters & Pins
  $("filterList").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-filter]");
    if (!btn) return;
    state.filterMode = btn.dataset.filter;
    state.brandFilter = "";
    renderAll();
  });
  $("brandList").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-brand]");
    if (!btn) return;
    state.brandFilter = state.brandFilter === btn.dataset.brand ? "" : btn.dataset.brand;
    renderAll();
  });
  $("btnSavePin").addEventListener("click", saveCurrentLocationPin);
  $("btnAtlasSavePin").addEventListener("click", saveCurrentLocationPin);
  $("savedPinsList").addEventListener("click", (e) => {
    const del = e.target.closest("[data-del-pin]");
    if (del) {
      e.stopPropagation();
      state.settings.savedLocations = (state.settings.savedLocations || []).filter((x) => x.id !== del.dataset.delPin);
      rpc("saveSettings", { savedLocations: state.settings.savedLocations });
      renderSidebar();
      return;
    }
    const row = e.target.closest("[data-pin-id]");
    if (row) {
      const pin = (state.settings.savedLocations || []).find((x) => x.id === row.dataset.pinId);
      if (pin) {
        $("gpsInput").value = `${pin.latitude}, ${pin.longitude}${pin.altitude != null ? `, alt=${pin.altitude}` : ""}`;
        $("coordSysSelect").value = "wgs84";
        validateGpsInput();
        switchInspTab("gps");
        showToast(`已载入地点：${pin.name}`, "info");
      }
    }
  });

  // Search, Sort, Grid sizing
  $("searchInput").addEventListener("input", (e) => { state.searchText = e.target.value; renderAll(); });
  $("sortSelect").addEventListener("change", (e) => { state.sortMode = e.target.value; renderAll(); });
  $("thumbSizeRange").addEventListener("input", (e) => {
    state.thumbSize = Number(e.target.value);
    if (state.viewMode === "grid") renderVirtualGrid();
  });
  $("thumbSizeRange").addEventListener("change", () => rpc("saveSettings", { thumbSize: state.thumbSize }));
  $("btnFitMode").addEventListener("click", () => {
    state.fitContain = !state.fitContain;
    document.body.classList.toggle("thumb-contain", state.fitContain);
    $("btnFitMode").textContent = state.fitContain ? "Contain" : "Cover";
  });

  $("gridView").addEventListener("scroll", () => requestAnimationFrame(renderVirtualGrid), { passive: true });
  window.addEventListener("resize", () => { if (state.viewMode === "grid") renderVirtualGrid(); });

  // Selection buttons
  $("btnSelAll").addEventListener("click", () => {
    for (const p of state.filtered) state.selectedIds.add(p.id);
    renderAll();
  });
  $("btnSelNone").addEventListener("click", () => { state.selectedIds.clear(); renderAll(); });
  $("btnSelInvert").addEventListener("click", () => {
    for (const p of state.filtered) {
      if (state.selectedIds.has(p.id)) state.selectedIds.delete(p.id);
      else state.selectedIds.add(p.id);
    }
    renderAll();
  });
  $("btnRemoveSel").addEventListener("click", async () => {
    if (state.selectedIds.size === 0) return;
    await rpc("removePhotos", { ids: [...state.selectedIds] });
  });
  $("btnClearAll").addEventListener("click", async () => {
    if (state.photos.length === 0) return;
    await rpc("clearPhotos");
  });

  // Card / Row interactions
  $("stageBody").addEventListener("click", (e) => {
    const chk = e.target.closest("[data-check]");
    if (chk) {
      e.stopPropagation();
      const id = chk.dataset.check;
      if (state.selectedIds.has(id)) state.selectedIds.delete(id);
      else state.selectedIds.add(id);
      state.focusedId = id;
      renderAll();
      return;
    }
    const item = e.target.closest("[data-id]");
    if (!item) return;
    const id = item.dataset.id;
    const idx = Number(item.dataset.idx);
    if (e.ctrlKey || e.metaKey) {
      if (state.selectedIds.has(id)) state.selectedIds.delete(id);
      else state.selectedIds.add(id);
    } else if (e.shiftKey && state.lastClickedIndex >= 0) {
      const [lo, hi] = [Math.min(state.lastClickedIndex, idx), Math.max(state.lastClickedIndex, idx)];
      for (let i = lo; i <= hi; i++) if (state.filtered[i]) state.selectedIds.add(state.filtered[i].id);
    } else {
      state.selectedIds.clear();
      state.selectedIds.add(id);
    }
    state.focusedId = id;
    state.lastClickedIndex = idx;
    renderAll();
  });

  $("stageBody").addEventListener("dblclick", (e) => {
    const item = e.target.closest("[data-id]");
    if (item && !e.target.closest("[data-check]")) openLoupe(item.dataset.id);
  });

  // Card drag-to-map geotagging
  $("gridView").addEventListener("dragstart", (e) => {
    const card = e.target.closest("[data-id]");
    if (card && e.dataTransfer) {
      e.dataTransfer.setData("text/photo-id", card.dataset.id);
    }
  });
  $("atlasMap").addEventListener("dragover", (e) => {
    if (e.dataTransfer?.types?.includes("text/photo-id")) e.preventDefault();
  });
  $("atlasMap").addEventListener("drop", async (e) => {
    const photoId = e.dataTransfer?.getData("text/photo-id");
    if (!photoId) return;
    e.preventDefault();
    const rect = $("atlasMap").getBoundingClientRect();
    const pt = atlas.pixelToLatLng(e.clientX - rect.left, e.clientY - rect.top);
    if (!pt) return;
    atlas.setPickedPoint(pt.lat, pt.lon, "", true);
    if (!state.selectedIds.has(photoId)) selectSingle(photoId);
    await executeWriteGps(pt.lat, pt.lon, null, "");
  });

  // Inspector Tabs & Quick Actions
  $("inspTabs").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-tab]");
    if (btn) switchInspTab(btn.dataset.tab);
  });
  $("inspThumbBox").addEventListener("click", () => { if (state.focusedId) openLoupe(state.focusedId); });
  $("btnOpenLoupe").addEventListener("click", () => { if (state.focusedId) openLoupe(state.focusedId); });
  $("btnRevealFile").addEventListener("click", () => { if (state.focusedId) rpc("revealInExplorer", { photoId: state.focusedId }); });
  $("btnCopyMeta").addEventListener("click", copyFocusedMeta);
  $("btnPasteMeta").addEventListener("click", pasteMetaToSelected);

  // GPS Panel
  $("gpsInput").addEventListener("input", validateGpsInput);
  $("coordSysSelect").addEventListener("change", validateGpsInput);
  $("btnPickOnMap").addEventListener("click", () => setViewMode("atlas", true));
  $("btnWriteGps").addEventListener("click", writeGpsToSelected);
  $("btnClearGps").addEventListener("click", () => runPrivacyStrip("gps"));
  $("btnExportFootprint").addEventListener("click", async () => {
    try {
      const res = await rpc("exportFootprint", { ids: [...state.selectedIds], format: $("exportFormatSelect").value });
      if (!res.cancelled) showToast(`已导出 ${res.count} 个足迹点`, "success");
    } catch (err) { showToast(err.message, "error"); }
  });

  // Atlas Controls
  $("btnAtlasSearch").addEventListener("click", runAtlasSearch);
  $("atlasSearchInput").addEventListener("keydown", (e) => { if (e.key === "Enter") runAtlasSearch(); });
  $("atlasLayerSelect").addEventListener("change", (e) => atlas.setLayer(e.target.value));
  $("chkAtlasTrack").addEventListener("change", (e) => atlas.setShowTrack(e.target.checked, state.photos, state.focusedId));
  $("btnAtlasFit").addEventListener("click", () => atlas.fitToPhotos(state.photos));
  $("btnAtlasUseCoord").addEventListener("click", () => {
    if (!atlas.pickedWgs) return;
    $("gpsInput").value = `${atlas.pickedWgs.lat.toFixed(6)}, ${atlas.pickedWgs.lon.toFixed(6)}`;
    $("coordSysSelect").value = "wgs84";
    validateGpsInput();
    switchInspTab("gps");
    showToast("坐标已填入定位面板", "info");
  });
  $("btnAtlasApplySel").addEventListener("click", async () => {
    if (!atlas.pickedWgs || state.selectedIds.size === 0) return;
    await executeWriteGps(atlas.pickedWgs.lat, atlas.pickedWgs.lon, null, atlas.pickedWgs.label || "");
  });

  // Date Studio
  $("btnSmartDiagnose").addEventListener("click", openDateDiagnosis);
  $("btnDiagClose").addEventListener("click", () => $("diagModal").hidden = true);
  $("btnDiagCancel").addEventListener("click", () => $("diagModal").hidden = true);
  $("btnDiagSelectActionable").addEventListener("click", () => {
    for (const chk of $("diagList").querySelectorAll("input[type=checkbox]"))
      chk.checked = chk.dataset.actionable === "1";
  });
  $("btnDiagSelectAll").addEventListener("click", () => {
    for (const chk of $("diagList").querySelectorAll("input[type=checkbox]")) chk.checked = true;
  });
  $("btnDiagApply").addEventListener("click", applyDateDiagnosis);

  for (const btn of document.querySelectorAll(".quick-shift")) {
    btn.addEventListener("click", () => {
      const sec = Number(btn.dataset.sec);
      const cur = getShiftTotalSeconds() + sec;
      setShiftFields(cur);
      updateShiftPreview();
    });
  }
  for (const id of ["shiftDays", "shiftHours", "shiftMins", "shiftSecs"]) {
    $(id).addEventListener("input", updateShiftPreview);
  }
  $("btnApplyShift").addEventListener("click", () => writeDateOperation("shift"));
  $("btnWriteExactDate").addEventListener("click", () => writeDateOperation("exact"));
  $("btnWriteSeqDate").addEventListener("click", () => writeDateOperation("sequence"));

  // Format, Camera & Privacy
  $("jpegQualityRange").addEventListener("input", (e) => $("jpegQualityVal").textContent = `${e.target.value}%`);
  $("btnFixExtensions").addEventListener("click", async () => {
    const targets = state.photos.filter((p) => p.isExtensionMismatched).map((p) => p.id);
    if (targets.length === 0) return;
    try {
      const res = await rpc("fixExtensions", { ids: targets });
      showToast(`已更正 ${res.renamed} 个异常后缀`, "success");
    } catch (err) { showToast(err.message, "error"); }
  });
  $("btnConvertFormat").addEventListener("click", async () => {
    if (state.selectedIds.size === 0) return;
    if (!(await ensureOutputDirIfCopy())) return;
    try {
      const res = await rpc("convertFormat", {
        ids: [...state.selectedIds],
        targetFormat: $("convertTargetSelect").value,
        quality: Number($("jpegQualityRange").value),
        maxDimension: Number($("maxDimSelect").value) || null,
        writeMode: state.writeMode,
        outputDir: state.settings.lastOutputDirectory
      });
      showToast(`已完成 ${res.converted} 个文件格式转换`, "success");
    } catch (err) { showToast(err.message, "error"); }
  });
  $("btnWriteCameraInfo").addEventListener("click", async () => {
    if (state.selectedIds.size === 0) return;
    if (!(await ensureOutputDirIfCopy())) return;
    try {
      const res = await rpc("writeCameraInfo", {
        ids: [...state.selectedIds],
        make: $("camMakeInput").value.trim() || null,
        model: $("camModelInput").value.trim() || null,
        lensModel: $("camLensInput").value.trim() || null,
        artist: $("camArtistInput").value.trim() || null,
        copyright: $("camCopyrightInput").value.trim() || null,
        writeMode: state.writeMode,
        outputDir: state.settings.lastOutputDirectory
      });
      showToast(`已更新 ${res.written} 个文件的相机/版权信息`, "success");
    } catch (err) { showToast(err.message, "error"); }
  });
  $("btnStripGps").addEventListener("click", () => runPrivacyStrip("gps"));
  $("btnStripSocial").addEventListener("click", () => runPrivacyStrip("privacyKeepExif"));
  $("btnStripFull").addEventListener("click", () => runPrivacyStrip("privacyFull"));

  // GPX & Reference Photo modals
  $("btnImportGpx").addEventListener("click", runImportGpx);
  $("btnGpxClose").addEventListener("click", () => $("gpxModal").hidden = true);
  $("btnGpxCancel").addEventListener("click", () => $("gpxModal").hidden = true);
  $("btnGpxApply").addEventListener("click", applyGpxMatches);

  $("btnRefPhoto").addEventListener("click", openRefModal);
  $("btnRefClose").addEventListener("click", () => $("refModal").hidden = true);
  $("btnRefPickLocal").addEventListener("click", async () => {
    try {
      const res = await rpc("pickReferenceFile");
      if (res.cancelled) return;
      if (res.hasGps) {
        $("gpsInput").value = `${res.lat}, ${res.lon}${res.alt != null ? `, alt=${res.alt}` : ""}`;
        $("coordSysSelect").value = "wgs84";
        validateGpsInput();
        showToast(`已从 ${res.fileName} 吸取 GPS`, "success");
      }
      if (res.dateTaken) $("dateExactInput").value = res.dateTaken;
      $("refModal").hidden = true;
    } catch (err) { showToast(err.message, "error"); }
  });

  // Loupe
  $("btnLoupeClose").addEventListener("click", () => $("loupeOverlay").hidden = true);
  $("btnLoupeMatte").addEventListener("click", () => {
    state.loupeMatte = !state.loupeMatte;
    $("loupeFrame").classList.toggle("art-matte", state.loupeMatte);
    $("btnLoupeMatte").classList.toggle("active", state.loupeMatte);
  });
  $("btnLoupeTags").addEventListener("click", () => {
    const hidden = $("loupeOverlay").classList.toggle("hide-tags");
    $("btnLoupeTags").classList.toggle("active", !hidden);
  });
  $("btnLoupeZoom").addEventListener("click", () => {
    state.loupeZoom = state.loupeZoom === 1 ? 2 : state.loupeZoom === 2 ? 3 : 1;
    if (state.loupeZoom === 1) { state.loupePanX = 0; state.loupePanY = 0; }
    applyLoupeTransform();
  });
  const loupeVp = document.querySelector(".loupe-viewport");
  if (loupeVp) {
    loupeVp.addEventListener("wheel", (e) => {
      if ($("loupeOverlay").hidden) return;
      e.preventDefault();
      const factor = e.deltaY < 0 ? 1.18 : 1 / 1.18;
      state.loupeZoom = Math.min(5, Math.max(1, Number((state.loupeZoom * factor).toFixed(2))));
      if (state.loupeZoom <= 1.01) { state.loupeZoom = 1; state.loupePanX = 0; state.loupePanY = 0; }
      applyLoupeTransform();
    }, { passive: false });

    let panning = false, startX = 0, startY = 0, baseX = 0, baseY = 0;
    loupeVp.addEventListener("pointerdown", (e) => {
      if (state.loupeZoom <= 1 || e.button !== 0) return;
      panning = true; startX = e.clientX; startY = e.clientY;
      baseX = state.loupePanX; baseY = state.loupePanY;
      loupeVp.setPointerCapture?.(e.pointerId);
    });
    loupeVp.addEventListener("pointermove", (e) => {
      if (!panning) return;
      state.loupePanX = baseX + (e.clientX - startX);
      state.loupePanY = baseY + (e.clientY - startY);
      applyLoupeTransform();
    });
    const endPan = () => { panning = false; };
    loupeVp.addEventListener("pointerup", endPan);
    loupeVp.addEventListener("pointercancel", endPan);
  }
  $("loupePrev").addEventListener("click", () => stepLoupe(-1));
  $("loupeNext").addEventListener("click", () => stepLoupe(1));
  $("loupeTagSearch").addEventListener("input", renderLoupeTags);

  // Settings
  $("btnSettings").addEventListener("click", openSettingsModal);
  $("btnSetClose").addEventListener("click", () => $("settingsModal").hidden = true);
  $("btnSetCancel").addEventListener("click", () => $("settingsModal").hidden = true);
  $("btnSetBrowseOut").addEventListener("click", async () => {
    const res = await rpc("browseOutputDir");
    if (res.path) $("setOutputDir").value = res.path;
  });
  $("btnSetSave").addEventListener("click", async () => {
    const payload = {
      lastWriteMode: Number($("setWriteMode").value),
      lastOutputDirectory: $("setOutputDir").value.trim(),
      mapProvider: $("setMapProvider").value,
      aMapJsKey: $("setAmapKey").value.trim(),
      aMapSecurityJsCode: $("setAmapSec").value.trim(),
      smartSkipConfirm: $("setSkipConfirm").checked
    };
    Object.assign(state.settings, payload);
    await rpc("saveSettings", payload);
    setWriteMode(payload.lastWriteMode === 0 ? "copy" : "direct", false);
    if (state.viewMode === "atlas") atlas.ensureInit(state.settings);
    $("settingsModal").hidden = true;
    showToast("设置已保存", "success");
  });

  $("btnCancelBusy").addEventListener("click", () => rpc("cancelBusy"));

  // Global Drag-Drop Files from OS
  let dragDepth = 0;
  window.addEventListener("dragenter", (e) => {
    if (e.dataTransfer?.types?.includes("Files")) {
      dragDepth++;
      $("dropCurtain").hidden = false;
    }
  });
  window.addEventListener("dragleave", () => {
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) $("dropCurtain").hidden = true;
  });
  window.addEventListener("dragover", (e) => e.preventDefault());
  window.addEventListener("drop", (e) => {
    dragDepth = 0;
    $("dropCurtain").hidden = true;
    if (e.dataTransfer?.files?.length && getIsDesktop()) {
      e.preventDefault();
      sendDropFiles(e.dataTransfer.files, $("chkSubfolders").checked);
    }
  });

  // Global Shortcuts
  window.addEventListener("keydown", (e) => {
    const inText = ["INPUT", "TEXTAREA", "SELECT"].includes(document.activeElement?.tagName);
    if (!$("loupeOverlay").hidden) {
      if (e.key === "Escape") { $("loupeOverlay").hidden = true; return; }
      if (e.key === "ArrowLeft") { stepLoupe(-1); e.preventDefault(); return; }
      if (e.key === "ArrowRight") { stepLoupe(1); e.preventDefault(); return; }
    }
    if (e.key === "Escape") {
      $("diagModal").hidden = true;
      $("gpxModal").hidden = true;
      $("refModal").hidden = true;
      $("settingsModal").hidden = true;
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") {
      e.preventDefault();
      $("searchInput").focus();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === "c" && !inText) {
      e.preventDefault();
      copyFocusedMeta();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === "v" && !inText) {
      e.preventDefault();
      pasteMetaToSelected();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z" && !inText) {
      e.preventDefault();
      runUndo();
      return;
    }
    if (inText) return;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "a") {
      e.preventDefault();
      for (const p of state.filtered) state.selectedIds.add(p.id);
      renderAll();
    } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "i") {
      e.preventDefault();
      $("btnSelInvert").click();
    } else if (e.code === "Space" && state.focusedId) {
      e.preventDefault();
      openLoupe(state.focusedId);
    } else if (e.key === "Delete" && state.selectedIds.size > 0) {
      e.preventDefault();
      $("btnRemoveSel").click();
    } else if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(e.key) && state.filtered.length > 0 && state.viewMode !== "atlas") {
      e.preventDefault();
      navigateByArrow(e.key);
    }
  });
}

/* ==========================================================================
   CORE WORKFLOW HANDLERS
   ========================================================================== */
async function validateGpsInput() {
  const input = $("gpsInput").value.trim();
  const box = $("gpsPreviewBox");
  if (!input) {
    state.parsedGps = null;
    box.className = "preview-box mono";
    box.textContent = t("gpsWaiting");
    renderInspector();
    if (state.viewMode === "grid") renderVirtualGrid();
    return;
  }
  try {
    const res = await rpc("parseGps", { input, system: $("coordSysSelect").value });
    if (res.ok) {
      state.parsedGps = res.wgs84;
      box.className = "preview-box ok mono";
      box.textContent = `WGS-84: ${res.wgs84.display} (${res.wgs84.latRef}, ${res.wgs84.lonRef})`;
    } else {
      state.parsedGps = null;
      box.className = "preview-box err";
      box.textContent = res.error;
    }
  } catch (err) {
    state.parsedGps = null;
    box.className = "preview-box err";
    box.textContent = err.message;
  }
  renderInspector();
  if (state.viewMode === "grid") renderVirtualGrid();
}

async function writeGpsToSelected() {
  if (!state.parsedGps || state.selectedIds.size === 0) return;
  await executeWriteGps(state.parsedGps.lat, state.parsedGps.lon, state.parsedGps.alt ?? null, "");
}

async function executeWriteGps(lat, lon, alt, placeName) {
  if (!(await ensureOutputDirIfCopy())) return;
  try {
    const res = await rpc("writeGps", {
      ids: [...state.selectedIds],
      lat,
      lon,
      alt,
      placeName,
      writeMode: state.writeMode,
      outputDir: state.settings.lastOutputDirectory
    });
    showToast(`GPS 已写入 ${res.written} 个文件`, "success");
  } catch (err) {
    showToast(err.message, "error");
  }
}

async function runPrivacyStrip(stripMode) {
  if (state.selectedIds.size === 0) return showToast("请先选择至少一张照片", "warn");
  if (!(await ensureOutputDirIfCopy())) return;
  try {
    const res = await rpc("clearMetadata", {
      ids: [...state.selectedIds],
      stripMode,
      writeMode: state.writeMode,
      outputDir: state.settings.lastOutputDirectory
    });
    showToast(`已清理 ${res.cleaned} 个文件元数据`, "success");
  } catch (err) { showToast(err.message, "error"); }
}

async function openDateDiagnosis() {
  if (state.photos.length === 0) return showToast("工作区暂无照片", "info");
  const ids = state.selectedIds.size > 0 ? [...state.selectedIds] : state.photos.map((p) => p.id);
  const res = await rpc("diagnoseDates", { ids });
  state.diagItems = res.items || [];
  const actionable = state.diagItems.filter((x) => Boolean(x.suggestedDate));
  $("diagSummary").textContent = `已分析 ${state.diagItems.length} 张 · 建议自动修复 ${actionable.length} 张`;
  $("diagList").innerHTML = state.diagItems.map((it) => {
    const act = Boolean(it.suggestedDate);
    return `
      <label class="sb-pin-row" style="padding:8px 10px;align-items:flex-start;">
        <input type="checkbox" data-diag-id="${esc(it.photoId)}" data-date="${esc(it.suggestedDate || "")}" data-actionable="${act ? "1" : "0"}" ${act ? "checked" : "disabled"} style="margin-top:3px;" />
        <div style="flex:1;min-width:0;">
          <div style="display:flex;justify-content:space-between;gap:8px;">
            <strong>${esc(it.fileName)}</strong>
            ${act ? `<span class="mono" style="color:var(--warn);">→ ${esc(it.suggestedDate)}</span>` : `<span style="color:var(--success);">✓</span>`}
          </div>
          <div class="mono" style="font-size:11px;color:var(--text-muted);">${esc(it.detail)} · EXIF: ${esc(it.exifDate || "无")}</div>
        </div>
      </label>
    `;
  }).join("");
  $("diagModal").hidden = false;
}

async function applyDateDiagnosis() {
  const chosen = [...$("diagList").querySelectorAll("input[type=checkbox]:checked")]
    .map((el) => ({ photoId: el.dataset.diagId, date: el.dataset.date }))
    .filter((x) => x.photoId && x.date);
  if (chosen.length === 0) return ($("diagModal").hidden = true);
  if (!(await ensureOutputDirIfCopy())) return;
  try {
    const res = await rpc("applyDateDiagnosis", {
      items: chosen,
      writeMode: state.writeMode,
      outputDir: state.settings.lastOutputDirectory,
      syncFileTimes: $("chkDiagSyncFiles").checked
    });
    $("diagModal").hidden = true;
    showToast(`已修复 ${res.written} 张照片时间`, "success");
  } catch (err) { showToast(err.message, "error"); }
}

async function writeDateOperation(mode) {
  if (state.selectedIds.size === 0) return showToast("请先选择照片", "warn");
  if (!(await ensureOutputDirIfCopy())) return;
  try {
    const res = await rpc("writeDate", {
      ids: [...state.selectedIds],
      mode,
      dateStr: $("dateExactInput").value.trim(),
      shiftSeconds: getShiftTotalSeconds(),
      sequenceStepSeconds: Number($("seqStepInput").value) || 1,
      writeMode: state.writeMode,
      outputDir: state.settings.lastOutputDirectory,
      syncFileTimes: $("chkSyncFileTimes").checked
    });
    showToast(`已写入 ${res.written} 张照片时间`, "success");
  } catch (err) { showToast(err.message, "error"); }
}

async function runImportGpx() {
  try {
    const res = await rpc("importGpx", {
      ids: [...state.selectedIds],
      toleranceMinutes: 30,
      offsetHours: 8
    });
    if (res.cancelled) return;
    state.gpxProposals = res.proposals || [];
    $("gpxSummary").textContent = `轨迹 ${res.fileName} (${res.pointCount} 点) · 成功匹配 ${state.gpxProposals.length} 张照片`;
    $("gpxMatchList").innerHTML = state.gpxProposals.length === 0
      ? `<div class="preview-box">未找到时间窗口 (±30分钟) 内的匹配照片。</div>`
      : state.gpxProposals.map((m) => `
        <label class="sb-pin-row">
          <input type="checkbox" data-gpx-id="${esc(m.photoId)}" checked />
          <span style="flex:1;"><strong>${esc(m.fileName)}</strong> <span class="mono" style="color:var(--text-muted);">${esc(m.captureTime)}</span></span>
          <span class="mono" style="color:var(--accent);">${m.latitude.toFixed(5)}, ${m.longitude.toFixed(5)}</span>
        </label>
      `).join("");
    $("gpxModal").hidden = false;
  } catch (err) { showToast(err.message, "error"); }
}

async function applyGpxMatches() {
  const checkedIds = new Set([...$("gpxMatchList").querySelectorAll("input:checked")].map((x) => x.dataset.gpxId));
  const matches = state.gpxProposals.filter((m) => checkedIds.has(m.photoId));
  if (matches.length === 0) return ($("gpxModal").hidden = true);
  if (!(await ensureOutputDirIfCopy())) return;
  try {
    const res = await rpc("applyGpxMatches", {
      matches: matches.map((m) => ({ photoId: m.photoId, lat: m.latitude, lon: m.longitude, alt: m.altitude })),
      writeMode: state.writeMode,
      outputDir: state.settings.lastOutputDirectory
    });
    $("gpxModal").hidden = true;
    showToast(`已按轨迹写入 ${res.written} 张 GPS`, "success");
  } catch (err) { showToast(err.message, "error"); }
}

function openRefModal() {
  const withGps = state.photos.filter((p) => p.hasGps);
  $("refLoadedList").innerHTML = withGps.length === 0
    ? `<div class="preview-box">当前工作区暂无带 GPS 的照片。</div>`
    : withGps.map((p) => `
      <div class="sb-pin-row" data-ref-id="${esc(p.id)}">
        <span style="flex:1;"><strong>${esc(p.fileName)}</strong></span>
        <span class="mono" style="color:var(--accent);">${esc(p.gpsDisplay)}</span>
      </div>
    `).join("");
  for (const el of $("refLoadedList").querySelectorAll("[data-ref-id]")) {
    el.addEventListener("click", () => {
      const p = state.photos.find((x) => x.id === el.dataset.refId);
      if (p) {
        $("gpsInput").value = `${p.latitude}, ${p.longitude}${p.altitude != null ? `, alt=${p.altitude}` : ""}`;
        $("coordSysSelect").value = "wgs84";
        validateGpsInput();
        if (p.dateTaken) $("dateExactInput").value = p.dateTaken;
        $("refModal").hidden = true;
        showToast(`已吸取 ${p.fileName} 坐标`, "success");
      }
    });
  }
  $("refModal").hidden = false;
}

async function runAtlasSearch() {
  const kw = $("atlasSearchInput").value.trim();
  if (!kw) return;
  const box = $("atlasSearchResults");
  box.hidden = false;
  box.innerHTML = `<div style="padding:8px;color:var(--text-muted);">搜索中...</div>`;
  const items = await atlas.search(kw);
  if (items.length === 0) {
    box.innerHTML = `<div style="padding:8px;color:var(--text-muted);">未找到匹配地点</div>`;
    return;
  }
  box.innerHTML = items.map((it, i) => `
    <div class="atlas-result-item" data-res-idx="${i}">
      <div class="atlas-result-name">${esc(it.name)}</div>
      <div class="atlas-result-sub">${esc(it.sub)}</div>
    </div>
  `).join("");
  box.onclick = (e) => {
    const r = e.target.closest("[data-res-idx]");
    if (!r) return;
    const item = items[Number(r.dataset.resIdx)];
    atlas.setPickedPoint(item.lat, item.lon, item.name, false, true);
    box.hidden = true;
  };
}

let cachedLoupeTags = {};
async function openLoupe(photoId) {
  const idx = state.filtered.findIndex((p) => p.id === photoId);
  if (idx < 0) return;
  state.loupeIndex = idx;
  state.loupeZoom = 1;
  state.loupePanX = 0;
  state.loupePanY = 0;
  applyLoupeTransform();
  $("loupeOverlay").hidden = false;
  await loadLoupeItem();
}

function applyLoupeTransform() {
  $("loupeImg").style.transform = `translate(${state.loupePanX}px, ${state.loupePanY}px) scale(${state.loupeZoom})`;
  $("loupeImg").style.cursor = state.loupeZoom > 1 ? "grab" : "default";
  $("btnLoupeZoom").textContent = `${Math.round(state.loupeZoom * 100)}%`;
}

function navigateByArrow(key) {
  const curIdx = state.filtered.findIndex((p) => p.id === state.focusedId);
  const cols = state.viewMode === "grid"
    ? Math.max(1, Number($("gridWindow").style.getPropertyValue("--cols")) || 1)
    : 1;
  let delta = 0;
  if (key === "ArrowLeft") delta = -1;
  else if (key === "ArrowRight") delta = 1;
  else if (key === "ArrowUp") delta = -cols;
  else if (key === "ArrowDown") delta = cols;

  const base = curIdx < 0 ? 0 : curIdx;
  const nextIdx = Math.max(0, Math.min(state.filtered.length - 1, base + delta));
  const target = state.filtered[nextIdx];
  if (!target) return;
  state.lastClickedIndex = nextIdx;
  selectSingle(target.id);
  renderAll();

  if (state.viewMode === "grid") {
    const container = $("gridView");
    const w = Math.max(320, container.clientWidth - 28);
    const gap = 14;
    const cardW = (w - gap * (cols - 1)) / cols;
    const rowH = Math.round(cardW * 0.75 + 72) + gap;
    const rowTop = Math.floor(nextIdx / cols) * rowH;
    const rowBottom = rowTop + rowH;
    if (rowTop < container.scrollTop) container.scrollTop = rowTop;
    else if (rowBottom > container.scrollTop + container.clientHeight) container.scrollTop = rowBottom - container.clientHeight;
  } else if (state.viewMode === "table") {
    const row = $("tableBody").querySelector(`tr[data-id="${CSS.escape(target.id)}"]`);
    row?.scrollIntoView({ block: "nearest" });
  }
}

function stepLoupe(delta) {
  const next = state.loupeIndex + delta;
  if (next < 0 || next >= state.filtered.length) return;
  state.loupeIndex = next;
  state.loupeZoom = 1;
  state.loupePanX = 0;
  state.loupePanY = 0;
  applyLoupeTransform();
  selectSingle(state.filtered[next].id);
  renderAll();
  loadLoupeItem();
}

async function loadLoupeItem() {
  const p = state.filtered[state.loupeIndex];
  if (!p) return;
  $("loupeTitle").textContent = `${p.fileName} (${state.loupeIndex + 1}/${state.filtered.length})`;
  $("loupeImg").src = getPreviewUrl(p);
  const exp = [p.focalLength, p.aperture, p.shutterSpeed, p.iso ? `ISO ${p.iso}` : null].filter(Boolean).join(" · ");
  $("loupeArtLeft").textContent = `${p.cameraDisplay || p.fileName} · ${p.lensModel || ""}`;
  $("loupeArtRight").textContent = `${exp}${p.hasGps ? " · " + p.gpsDisplay : ""}`;

  $("loupeTagsList").innerHTML = `<div style="color:var(--text-muted);">Loading EXIF...</div>`;
  try {
    const res = await rpc("readAllTags", { photoId: p.id });
    cachedLoupeTags = res.tags || {};
    renderLoupeTags();
  } catch {
    cachedLoupeTags = {};
    renderLoupeTags();
  }
}

function renderLoupeTags() {
  const q = $("loupeTagSearch").value.trim().toLowerCase();
  const entries = Object.entries(cachedLoupeTags)
    .filter(([k, v]) => !q || k.toLowerCase().includes(q) || String(v).toLowerCase().includes(q));
  $("loupeTagsList").innerHTML = entries.map(([k, v]) => `
    <div class="exif-tag-row">
      <span class="exif-tag-k" title="${esc(k)}">${esc(k)}</span>
      <span class="exif-tag-v">${esc(v)}</span>
    </div>
  `).join("");
}

/* ==========================================================================
   HELPERS
   ========================================================================== */
function applyTheme(themeId, persist) {
  atmosphere.setTheme(themeId);
  state.settings.theme = themeId;
  for (const el of $("themeDots").querySelectorAll(".theme-dot-btn"))
    el.classList.toggle("active", el.dataset.theme === themeId);
  if (persist) rpc("saveSettings", { theme: themeId });
}

function setViewMode(mode, persist) {
  state.viewMode = mode;
  for (const b of $("viewSeg").querySelectorAll("[data-view]"))
    b.classList.toggle("active", b.dataset.view === mode);
  if (persist) rpc("saveSettings", { viewMode: mode });
  renderMainStage();
  renderToolbar();
  if (mode === "atlas") {
    atlas.ensureInit(state.settings);
    atlas.syncWorkspacePhotos(state.photos, state.focusedId);
  }
}

function setWriteMode(mode, persist) {
  state.writeMode = mode === "copy" ? "copy" : "direct";
  for (const b of $("writeModeSeg").querySelectorAll("[data-wmode]")) {
    const active = b.dataset.wmode === state.writeMode;
    b.classList.toggle("active", active);
    b.classList.toggle("mode-copy", active && state.writeMode === "copy");
    b.classList.toggle("mode-direct", active && state.writeMode === "direct");
  }
  if (persist) rpc("saveSettings", { lastWriteMode: state.writeMode === "copy" ? 0 : 2 });
}

function switchInspTab(tab) {
  for (const b of $("inspTabs").querySelectorAll("[data-tab]"))
    b.classList.toggle("active", b.dataset.tab === tab);
  $("paneGps").hidden = tab !== "gps";
  $("paneDate").hidden = tab !== "date";
  $("paneFormat").hidden = tab !== "format";
}

async function ensureOutputDirIfCopy() {
  if (state.writeMode !== "copy") return true;
  if (state.settings.lastOutputDirectory) return true;
  const res = await rpc("browseOutputDir");
  if (res.path) {
    state.settings.lastOutputDirectory = res.path;
    return true;
  }
  showToast("请先选择安全副本输出目录", "warn");
  return false;
}

async function runUndo() {
  if (!state.undo.canUndo) return;
  try {
    const res = await rpc("undo");
    if (res.undone) showToast(`已撤销：${res.description}`, "success");
  } catch (err) { showToast(err.message, "error"); }
}

function copyFocusedMeta() {
  const p = state.photos.find((x) => x.id === state.focusedId);
  if (!p) return;
  state.copiedMeta = { lat: p.latitude, lon: p.longitude, alt: p.altitude, dateTaken: p.dateTaken };
  if (p.hasGps) {
    $("gpsInput").value = `${p.latitude}, ${p.longitude}${p.altitude != null ? `, alt=${p.altitude}` : ""}`;
    $("coordSysSelect").value = "wgs84";
    validateGpsInput();
  }
  if (p.dateTaken) $("dateExactInput").value = p.dateTaken;
  renderInspector();
  showToast(`已吸取 ${p.fileName} 参数`, "success");
}

async function pasteMetaToSelected() {
  if (!state.copiedMeta || state.selectedIds.size === 0) return;
  if (state.copiedMeta.lat != null && state.copiedMeta.lon != null) {
    await executeWriteGps(state.copiedMeta.lat, state.copiedMeta.lon, state.copiedMeta.alt, "");
  }
  if (state.copiedMeta.dateTaken) {
    $("dateExactInput").value = state.copiedMeta.dateTaken;
    await writeDateOperation("exact");
  }
}

function saveCurrentLocationPin() {
  const src = atlas.pickedWgs || state.parsedGps;
  if (!src) return showToast("请先选择或输入一个有效坐标", "warn");
  const name = src.label || atlas.pickedWgs?.label || `${src.lat.toFixed(4)}, ${src.lon.toFixed(4)}`;
  const pin = { id: `pin_${Date.now()}`, name, latitude: src.lat, longitude: src.lon, altitude: src.alt ?? null };
  state.settings.savedLocations = [pin, ...(state.settings.savedLocations || [])].slice(0, 12);
  rpc("saveSettings", { savedLocations: state.settings.savedLocations });
  renderSidebar();
  showToast(`已收藏地点：${name}`, "success");
}

function openSettingsModal() {
  $("setWriteMode").value = state.writeMode === "copy" ? "0" : "2";
  $("setOutputDir").value = state.settings.lastOutputDirectory || "";
  $("setMapProvider").value = state.settings.mapProvider || "osm";
  $("setAmapKey").value = state.settings.aMapJsKey || "";
  $("setAmapSec").value = state.settings.aMapSecurityJsCode || "";
  $("setSkipConfirm").checked = Boolean(state.settings.smartSkipConfirm);
  $("settingsModal").hidden = false;
}

function getShiftTotalSeconds() {
  const d = Number($("shiftDays").value) || 0;
  const h = Number($("shiftHours").value) || 0;
  const m = Number($("shiftMins").value) || 0;
  const s = Number($("shiftSecs").value) || 0;
  return d * 86400 + h * 3600 + m * 60 + s;
}

function setShiftFields(totalSec) {
  const sign = totalSec < 0 ? -1 : 1;
  let abs = Math.abs(totalSec);
  const d = Math.floor(abs / 86400); abs %= 86400;
  const h = Math.floor(abs / 3600); abs %= 3600;
  const m = Math.floor(abs / 60); const s = abs % 60;
  $("shiftDays").value = d * sign;
  $("shiftHours").value = h * sign;
  $("shiftMins").value = m * sign;
  $("shiftSecs").value = s * sign;
}

function updateShiftPreview() {
  const photo = state.photos.find((p) => p.id === state.focusedId);
  const sec = getShiftTotalSeconds();
  if (!photo || !photo.dateTaken) {
    $("shiftPreviewBox").textContent = t("noFocusedDate");
    return;
  }
  const iso = photo.dateTaken.replace(/^(\d{4}):(\d{2}):(\d{2})/, "$1-$2-$3");
  const dt = new Date(iso);
  if (Number.isNaN(dt.getTime())) {
    $("shiftPreviewBox").textContent = photo.dateTaken;
    return;
  }
  const next = new Date(dt.getTime() + sec * 1000);
  const fmt = `${next.getFullYear()}:${pad2(next.getMonth() + 1)}:${pad2(next.getDate())} ${pad2(next.getHours())}:${pad2(next.getMinutes())}:${pad2(next.getSeconds())}`;
  $("shiftPreviewBox").innerHTML = `<div>${esc(photo.dateTaken)}</div><div style="color:var(--accent);">→ ${esc(fmt)}</div>`;
}

function selectSingle(id) {
  state.selectedIds.clear();
  state.selectedIds.add(id);
  state.focusedId = id;
}

function showToast(message, kind = "info") {
  const stack = $("toastStack");
  const el = document.createElement("div");
  el.className = "toast-pill";
  el.innerHTML = `${icon(kind === "error" ? "alert" : "check")}<span>${esc(message)}</span>`;
  stack.appendChild(el);
  setTimeout(() => el.remove(), 3200);
}

function injectStaticIcons() {
  $("btnToggleSidebar").innerHTML = icon("sidebar");
  $("brandMark").innerHTML = icon("aperture");
  $("searchIconSlot").innerHTML = icon("search");
  $("emptyIconSlot").innerHTML = icon("image");
  $("btnWinMin").innerHTML = icon("min");
  $("btnWinMax").innerHTML = icon("max");
  $("btnWinClose").innerHTML = icon("close");
  $("loupePrev").innerHTML = icon("chevL");
  $("loupeNext").innerHTML = icon("chevR");
}

function buildThemeDots() {
  $("themeDots").innerHTML = THEMES.map((th) =>
    `<button class="theme-dot-btn" data-theme="${th.id}" style="background:${th.dot};" title="${th.zh} · ${th.en}"></button>`
  ).join("");
}

function applyLanguage() {
  for (const el of document.querySelectorAll("[data-i18n]")) {
    const k = el.dataset.i18n;
    el.textContent = t(k);
  }
  $("viewSeg").querySelector('[data-view="grid"]').innerHTML = `${icon("grid")}<span>${t("viewGrid")}</span>`;
  $("viewSeg").querySelector('[data-view="table"]').innerHTML = `${icon("list")}<span>${t("viewTable")}</span>`;
  $("viewSeg").querySelector('[data-view="atlas"]').innerHTML = `${icon("map")}<span>${t("viewAtlas")}</span>`;
  $("writeModeSeg").querySelector('[data-wmode="copy"]').textContent = t("writeCopy");
  $("writeModeSeg").querySelector('[data-wmode="direct"]').textContent = t("writeDirect");
  $("btnPureDarkroom").innerHTML = `${icon("moon")}<span>${t("pureDarkroom")}</span>`;
  $("btnSettings").innerHTML = `${icon("settings")}<span>${t("settings")}</span>`;

  $("btnAddFiles").innerHTML = `${icon("image")}<span>${t("addPhotos")}</span>`;
  $("btnAddFolder").innerHTML = `${icon("folder")}<span>${t("addFolder")}</span>`;
  $("btnEmptyFiles").innerHTML = `${icon("image")}<span>${t("addPhotos")}</span>`;
  $("btnEmptyFolder").innerHTML = `${icon("folder")}<span>${t("addFolder")}</span>`;
  $("btnSavePin").textContent = t("saveCurrentPin");

  $("searchInput").placeholder = t("searchPlaceholder");
  $("sortSelect").options[0].textContent = t("sortDateDesc");
  $("sortSelect").options[1].textContent = t("sortDateAsc");
  $("sortSelect").options[2].textContent = t("sortNameAsc");
  $("sortSelect").options[3].textContent = t("sortCamera");

  $("btnSelAll").textContent = t("selectAll");
  $("btnSelNone").textContent = t("selectNone");
  $("btnSelInvert").textContent = t("invertSel");
  $("btnRemoveSel").textContent = t("removeSel");
  $("btnClearAll").textContent = t("clearAll");

  $("btnCopyMeta").innerHTML = `${icon("copy")}<span>${t("copyMeta")}</span>`;
  $("btnPasteMeta").innerHTML = `${icon("check")}<span>${t("pasteMeta")}</span>`;
  $("btnRevealFile").innerHTML = `${icon("external")}<span>${t("revealFile")}</span>`;
  $("btnOpenLoupe").innerHTML = `${icon("eye")}<span>${t("openLoupe")}</span>`;

  $("inspTabs").querySelector('[data-tab="gps"]').textContent = t("tabGps");
  $("inspTabs").querySelector('[data-tab="date"]').textContent = t("tabDate");
  $("inspTabs").querySelector('[data-tab="format"]').textContent = t("tabFormat");

  $("gpsInput").placeholder = t("gpsPlaceholder");
  $("btnPickOnMap").innerHTML = `${icon("map")}<span>${t("pickOnMap")}</span>`;
  $("btnRefPhoto").innerHTML = `${icon("camera")}<span>${t("refPhoto")}</span>`;
  $("btnImportGpx").innerHTML = `${icon("route")}<span>${t("gpxImport")}</span>`;
  $("btnClearGps").textContent = t("clearGpsBtn");
  $("btnExportFootprint").innerHTML = `${icon("download")}<span>${t("exportFootprint")}</span>`;

  $("btnSmartDiagnose").innerHTML = `${icon("sparkles")}<span>${t("dateSmartBtn")}</span>`;
  $("btnFixExtensions").textContent = t("fixExtBtn");
  $("btnWriteCameraInfo").textContent = t("writeCameraBtn", state.selectedIds.size);
  $("btnStripGps").textContent = t("stripGpsBtn");
  $("btnStripSocial").textContent = t("stripSocialBtn");
  $("btnStripFull").textContent = t("stripAllBtn");

  $("atlasSearchInput").placeholder = t("atlasSearchPh");
  $("btnAtlasSearch").innerHTML = icon("search");
  $("btnAtlasSavePin").innerHTML = `${icon("star")}<span>${t("saveCurrentPin")}</span>`;
  $("btnAtlasUseCoord").textContent = t("atlasUseCoord");
  if (!state.parsedGps) $("gpsPreviewBox").textContent = t("gpsWaiting");
  if (!atlas.pickedWgs) {
    $("atlasCoordReadout").textContent = t("atlasClickHint");
    $("atlasPlaceReadout").textContent = t("atlasSubHint");
  }
  if (typeof state.exifToolReady === "boolean") {
    $("daemonBadgeText").textContent = state.exifToolReady ? t("daemonReady") : t("daemonOff");
  }
  const sysOpts = $("coordSysSelect").options;
  if (sysOpts.length >= 3) {
    sysOpts[1].text = getLanguage() === "en" ? "GCJ-02 (AMap / Tencent / CN Apple)" : "GCJ-02 (高德 / 腾讯 / 国内 Apple)";
    sysOpts[2].text = getLanguage() === "en" ? "BD-09 (Baidu Map)" : "BD-09 (百度地图)";
  }
}

function formatDms(lat, lon) {
  const part = (v, p, n) => {
    const a = Math.abs(v);
    const d = Math.floor(a);
    const mf = (a - d) * 60;
    const m = Math.floor(mf);
    const s = ((mf - m) * 60).toFixed(1);
    return `${d}°${pad2(m)}'${s}"${v >= 0 ? p : n}`;
  };
  return `${part(lat, "N", "S")} ${part(lon, "E", "W")}`;
}

const pad2 = (n) => String(n).padStart(2, "0");
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]));

bootstrap();
