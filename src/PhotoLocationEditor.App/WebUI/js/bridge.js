/* ==========================================================================
   DESKTOP WEBVIEW2 RPC BRIDGE + STANDALONE BROWSER DEMO FALLBACK
   ========================================================================== */

const isDesktop = Boolean(typeof window !== "undefined" && window.chrome && window.chrome.webview);
const pending = new Map();
const listeners = new Map();
let seq = 0;

if (isDesktop) {
  window.chrome.webview.addEventListener("message", (event) => {
    const msg = event.data;
    if (!msg) return;
    if (msg.kind === "response" && msg.reqId) {
      const entry = pending.get(msg.reqId);
      if (entry) {
        pending.delete(msg.reqId);
        if (msg.ok) entry.resolve(msg.data);
        else entry.reject(new Error(msg.error || "Unknown host error"));
      }
    } else if (msg.kind === "event" && msg.eventName) {
      emitLocal(msg.eventName, msg.data);
    }
  });
}

function emitLocal(eventName, data) {
  const fns = listeners.get(eventName);
  if (!fns) return;
  for (const fn of fns) fn(data);
}

export function onHostEvent(eventName, fn) {
  if (!listeners.has(eventName)) listeners.set(eventName, new Set());
  listeners.get(eventName).add(fn);
  return () => listeners.get(eventName)?.delete(fn);
}

export function getIsDesktop() {
  return isDesktop;
}

export function getThumbUrl(photo) {
  if (!isDesktop && photo._demoThumb) return photo._demoThumb;
  return `https://media.photoinfo.local/thumb?id=${encodeURIComponent(photo.id)}&w=420&v=${photo._rev || 0}`;
}

export function getPreviewUrl(photo) {
  if (!isDesktop && photo._demoThumb) return photo._demoThumb;
  return `https://media.photoinfo.local/preview?id=${encodeURIComponent(photo.id)}&v=${photo._rev || 0}`;
}

export function sendDropFiles(fileList, includeSubfolders) {
  if (!isDesktop) return;
  const reqId = `req_${++seq}`;
  window.chrome.webview.postMessageWithAdditionalObjects(
    { type: "dropFiles", reqId, payload: { includeSubfolders } },
    fileList
  );
}

export function rpc(type, payload = {}) {
  if (isDesktop) {
    const reqId = `req_${++seq}`;
    return new Promise((resolve, reject) => {
      pending.set(reqId, { resolve, reject });
      window.chrome.webview.postMessage({ type, reqId, payload });
    });
  }
  return mockRpc(type, payload);
}

/* ==========================================================================
   BROWSER DEMO FALLBACK (for standalone visual QA in standard browsers)
   ========================================================================== */
function makeArtSvg(c1, c2, c3, sunX, sunY, title) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 600">
    <defs>
      <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0%" stop-color="${c1}"/><stop offset="65%" stop-color="${c2}"/><stop offset="100%" stop-color="${c3}"/>
      </linearGradient>
      <radialGradient id="glow" cx="${sunX}%" cy="${sunY}%" r="48%">
        <stop offset="0%" stop-color="#fff8e1" stop-opacity="0.92"/>
        <stop offset="45%" stop-color="${c3}" stop-opacity="0.35"/>
        <stop offset="100%" stop-color="${c1}" stop-opacity="0"/>
      </radialGradient>
    </defs>
    <rect width="800" height="600" fill="url(#sky)"/>
    <rect width="800" height="600" fill="url(#glow)"/>
    <path d="M0 430 Q140 350 280 410 T570 380 T800 420 L800 600 L0 600 Z" fill="#0d131d" opacity="0.58"/>
    <path d="M0 480 Q210 415 410 465 T800 450 L800 600 L0 600 Z" fill="#070a10" opacity="0.82"/>
    <text x="28" y="42" fill="rgba(255,255,255,0.44)" font-family="monospace" font-size="16">${title}</text>
  </svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

const demoSettings = {
  aMapJsKey: "",
  aMapSecurityJsCode: "",
  lastWriteMode: 2,
  lastOutputDirectory: "C:\\Photos\\Output",
  lastLanguage: "zh",
  theme: "obsidian",
  pureDarkroomMode: false,
  viewMode: "grid",
  mapProvider: "osm",
  smartSkipConfirm: false,
  syncFileTimestamps: false,
  jpegQuality: 92,
  thumbSize: 230,
  savedLocations: [
    { id: "pin1", name: "北京 · 景山万春亭", latitude: 39.9261, longitude: 116.3968, altitude: 88 }
  ],
  recentLocations: []
};

let demoPhotos = [
  {
    id: "d1", path: "D:\\Trip\\Kyoto\\DSCF2048.JPG", fileName: "DSCF2048.JPG", directory: "D:\\Trip\\Kyoto",
    cameraMake: "FUJIFILM", cameraModel: "X-T5", cameraDisplay: "FUJIFILM X-T5", lensModel: "XF23mmF1.4 R LM WR",
    focalLength: "23mm", aperture: "f/2", shutterSpeed: "1/320s", iso: 160, imageWidth: 7728, imageHeight: 5152,
    dateTaken: "2025:10:18 17:42:15", dateTakenDate: "2025:10:18", dateTakenTime: "17:42",
    fileCreationTime: "2025-10-18 17:42:16", latitude: 35.0116, longitude: 135.7681, altitude: 52,
    gpsDisplay: "35.0116, 135.7681, 52 m", hasGps: true, format: "JPEG", isVideo: false, isMetadataWritable: true,
    isExtensionMismatched: false, wrongExtensionHint: "", status: "Loaded",
    solar: { elevationDegrees: 4.2, azimuthDegrees: 256.4, phase: "GoldenHour", compassDirection: "WSW" },
    _demoThumb: makeArtSvg("#1b2845", "#5a4678", "#f4a261", 72, 34, "FUJIFILM X-T5 · 23mm")
  },
  {
    id: "d2", path: "D:\\Trip\\Dunhuang\\IMG_20250922_181509.jpg", fileName: "IMG_20250922_181509.jpg", directory: "D:\\Trip\\Dunhuang",
    cameraMake: "SONY", cameraModel: "ILCE-7RM5", cameraDisplay: "SONY ILCE-7RM5", lensModel: "FE 35mm F1.4 GM",
    focalLength: "35mm", aperture: "f/2.8", shutterSpeed: "1/1000s", iso: 100, imageWidth: 9504, imageHeight: 6336,
    dateTaken: null, dateTakenDate: null, dateTakenTime: null,
    fileCreationTime: "2025-09-22 18:15:10", latitude: null, longitude: null, altitude: null,
    gpsDisplay: "-", hasGps: false, format: "JPEG", isVideo: false, isMetadataWritable: true,
    isExtensionMismatched: false, wrongExtensionHint: "", status: "Loaded", solar: null,
    _demoThumb: makeArtSvg("#22384e", "#8f5132", "#f6bd60", 30, 38, "SONY A7R5 · 35mm GM")
  },
  {
    id: "d3", path: "D:\\Trip\\Shanghai\\mmexport1728468120000.png", fileName: "mmexport1728468120000.png", directory: "D:\\Trip\\Shanghai",
    cameraMake: "Apple", cameraModel: "iPhone 16 Pro", cameraDisplay: "Apple iPhone 16 Pro", lensModel: "24mm f/1.78",
    focalLength: "24mm", aperture: "f/1.8", shutterSpeed: "1/60s", iso: 400, imageWidth: 4032, imageHeight: 3024,
    dateTaken: null, dateTakenDate: null, dateTakenTime: null,
    fileCreationTime: "2024-10-10 12:00:00", latitude: 31.2304, longitude: 121.4737, altitude: 18,
    gpsDisplay: "31.2304, 121.4737, 18 m", hasGps: true, format: "JPEG", isVideo: false, isMetadataWritable: true,
    isExtensionMismatched: true, wrongExtensionHint: "实际: JPEG, 后缀: .png", status: "Loaded", solar: null,
    _demoThumb: makeArtSvg("#091629", "#1d4e78", "#8fd3f4", 65, 24, "SHANGHAI BUND · MISMATCH")
  },
  {
    id: "d4", path: "D:\\Trip\\Sakura\\R0004120.DNG", fileName: "R0004120.DNG", directory: "D:\\Trip\\Sakura",
    cameraMake: "RICOH IMAGING", cameraModel: "RICOH GR IIIx", cameraDisplay: "RICOH GR IIIx", lensModel: "GR Lens 26.1mm F2.8",
    focalLength: "40mm", aperture: "f/4", shutterSpeed: "1/500s", iso: 200, imageWidth: 6000, imageHeight: 4000,
    dateTaken: "2025:04:04 06:28:44", dateTakenDate: "2025:04:04", dateTakenTime: "06:28",
    fileCreationTime: "2025-04-04 06:28:45", latitude: 35.6852, longitude: 139.7100, altitude: 38,
    gpsDisplay: "35.6852, 139.71, 38 m", hasGps: true, format: "DNG", isVideo: false, isMetadataWritable: true,
    isExtensionMismatched: false, wrongExtensionHint: "", status: "Loaded",
    solar: { elevationDegrees: 9.8, azimuthDegrees: 84.2, phase: "GoldenHour", compassDirection: "E" },
    _demoThumb: makeArtSvg("#e6bcc6", "#d98298", "#fff0f3", 55, 28, "RICOH GR IIIx · DNG")
  },
  {
    id: "d5", path: "D:\\Trip\\Tibet\\DJI_20250712_205210.MOV", fileName: "DJI_20250712_205210.MOV", directory: "D:\\Trip\\Tibet",
    cameraMake: "DJI", cameraModel: "Mavic 3 Pro", cameraDisplay: "DJI Mavic 3 Pro", lensModel: "Hasselblad L2D-20c",
    focalLength: "24mm", aperture: "f/2.8", shutterSpeed: "1/120s", iso: 100, imageWidth: 3840, imageHeight: 2160,
    dateTaken: "2025:07:12 20:52:10", dateTakenDate: "2025:07:12", dateTakenTime: "20:52",
    fileCreationTime: "2025-07-12 20:52:11", latitude: 30.7280, longitude: 90.9620, altitude: 4720,
    gpsDisplay: "30.728, 90.962, 4720 m", hasGps: true, format: "MOV", isVideo: true, isMetadataWritable: true,
    isExtensionMismatched: false, wrongExtensionHint: "", status: "Loaded",
    solar: { elevationDegrees: -5.2, azimuthDegrees: 294.1, phase: "BlueHour", compassDirection: "WNW" },
    _demoThumb: makeArtSvg("#0b0718", "#2d1d54", "#8af3d6", 48, 30, "DJI MAVIC 3 PRO · MOV")
  },
  {
    id: "d6", path: "D:\\Trip\\Beijing\\DSC08812.JPG", fileName: "DSC08812.JPG", directory: "D:\\Trip\\Beijing",
    cameraMake: "HUAWEI", cameraModel: "Pura 70 Ultra", cameraDisplay: "HUAWEI Pura 70 Ultra", lensModel: "23mm f/1.6",
    focalLength: "23mm", aperture: "f/1.6", shutterSpeed: "1/2400s", iso: 50, imageWidth: 4096, imageHeight: 2732,
    dateTaken: "2025:11:03 13:18:05", dateTakenDate: "2025:11:03", dateTakenTime: "13:18",
    fileCreationTime: "2025-11-03 13:18:05", latitude: null, longitude: null, altitude: null,
    gpsDisplay: "-", hasGps: false, format: "JPEG", isVideo: false, isMetadataWritable: true,
    isExtensionMismatched: false, wrongExtensionHint: "", status: "Loaded", solar: null,
    _demoThumb: makeArtSvg("#0d1522", "#263952", "#c9d8ee", 52, 22, "HUAWEI PURA 70 ULTRA")
  }
];

let demoUndo = null;

async function mockRpc(type, payload) {
  await new Promise((r) => setTimeout(r, 40));
  switch (type) {
    case "init":
      return {
        appVersion: "1.0.0",
        exifToolReady: true,
        settings: structuredClone(demoSettings),
        photos: structuredClone(demoPhotos),
        undo: demoUndo ? { canUndo: true, count: 1, description: demoUndo.desc } : { canUndo: false, count: 0 },
        windowMaximized: false
      };
    case "saveSettings":
      Object.assign(demoSettings, payload);
      return { saved: true };
    case "parseGps": {
      const m = (payload.input || "").match(/(-?\d+(?:\.\d+)?)[,\s]+(-?\d+(?:\.\d+)?)/);
      if (!m) return { ok: false, error: "Enter valid lat, lon (e.g. 39.9042, 116.4074)" };
      const lat = parseFloat(m[1]), lon = parseFloat(m[2]);
      if (Math.abs(lat) > 90 || Math.abs(lon) > 180) return { ok: false, error: "Out of coordinate bounds." };
      return {
        ok: true,
        wgs84: { lat, lon, alt: null, display: `${lat.toFixed(6)}, ${lon.toFixed(6)}`, latRef: lat >= 0 ? "N" : "S", lonRef: lon >= 0 ? "E" : "W", dms: `${Math.abs(lat).toFixed(4)}° ${lat >= 0 ? "N" : "S"}` },
        gcj02: { lat: lat + 0.0012, lon: lon + 0.0061 }
      };
    }
    case "writeGps": {
      const ids = new Set(payload.ids || []);
      demoUndo = { desc: `GPS (${ids.size})`, snap: structuredClone(demoPhotos) };
      for (const p of demoPhotos) {
        if (ids.has(p.id)) {
          p.latitude = payload.lat; p.longitude = payload.lon; p.altitude = payload.alt ?? p.altitude;
          p.hasGps = true; p.gpsDisplay = `${payload.lat.toFixed(6)}, ${payload.lon.toFixed(6)}`;
          p.status = "GPS Written";
        }
      }
      emitLocal("photosUpdated", { photos: structuredClone(demoPhotos), undo: { canUndo: true, count: 1, description: demoUndo.desc } });
      return { written: ids.size, skipped: [] };
    }
    case "diagnoseDates":
      return {
        items: demoPhotos.map((p) => ({
          photoId: p.id, path: p.path, fileName: p.fileName,
          exifDate: p.dateTaken, filenameDate: p.fileName.includes("20250922") ? "2025:09:22 18:15:09" : (p.fileName.includes("1728468120000") ? "2024:10:09 18:02:00" : null),
          suggestedDate: !p.dateTaken ? (p.fileName.includes("20250922") ? "2025:09:22 18:15:09" : "2024:10:09 18:02:00") : null,
          category: !p.dateTaken ? 1 : 0,
          detail: !p.dateTaken ? "无EXIF → 从文件名精准还原" : "时间正常"
        }))
      };
    case "applyDateDiagnosis": {
      for (const it of payload.items || []) {
        const p = demoPhotos.find((x) => x.id === it.photoId);
        if (p) { p.dateTaken = it.date; p.dateTakenDate = it.date.slice(0, 10); p.dateTakenTime = it.date.slice(11, 16); p.status = "Date Fixed"; }
      }
      emitLocal("photosUpdated", { photos: structuredClone(demoPhotos), undo: { canUndo: false, count: 0 } });
      return { written: (payload.items || []).length, skipped: [] };
    }
    case "fixExtensions": {
      const ids = new Set(payload.ids || []);
      let renamed = 0;
      for (const p of demoPhotos) {
        if (ids.has(p.id) && p.isExtensionMismatched) {
          p.fileName = p.fileName.replace(/\.[^.]+$/, ".jpg");
          p.isExtensionMismatched = false; p.wrongExtensionHint = ""; p.status = "Renamed"; renamed++;
        }
      }
      emitLocal("photosUpdated", { photos: structuredClone(demoPhotos), undo: { canUndo: false, count: 0 } });
      return { renamed, skipped: 0 };
    }
    case "readAllTags":
      return {
        tags: {
          "ExifIFD:Make": "FUJIFILM", "ExifIFD:Model": "X-T5", "ExifIFD:LensModel": "XF23mmF1.4 R LM WR",
          "ExifIFD:ExposureTime": "1/320", "ExifIFD:FNumber": "2.0", "ExifIFD:ISO": "160",
          "ExifIFD:DateTimeOriginal": "2025:10:18 17:42:15", "ExifIFD:ColorSpace": "sRGB",
          "GPS:GPSLatitude": "35.0116", "GPS:GPSLongitude": "135.7681"
        }
      };
    case "undo":
      if (demoUndo) {
        demoPhotos = demoUndo.snap;
        const desc = demoUndo.desc;
        demoUndo = null;
        emitLocal("photosUpdated", { photos: structuredClone(demoPhotos), undo: { canUndo: false, count: 0 } });
        return { undone: true, description: desc };
      }
      return { undone: false };
    case "removePhotos": {
      const ids = new Set(payload.ids || []);
      demoPhotos = demoPhotos.filter((p) => !ids.has(p.id));
      emitLocal("photosUpdated", { photos: structuredClone(demoPhotos), undo: { canUndo: false, count: 0 } });
      return { removed: ids.size };
    }
    case "clearPhotos":
      demoPhotos = [];
      emitLocal("photosUpdated", { photos: [], undo: { canUndo: false, count: 0 } });
      return { cleared: true };
    default:
      return { ok: true };
  }
}
