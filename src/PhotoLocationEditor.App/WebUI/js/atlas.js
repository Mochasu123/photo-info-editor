/* ==========================================================================
   SPATIAL ATLAS — UNIFIED LEAFLET (OSM / TOPO / SATELLITE) + AMAP 2.0 ENGINE
   Supports workspace photo markers, footprint polyline, GPX preview, & search
   ========================================================================== */

export class SpatialAtlas {
  constructor(containerId, callbacks) {
    this.containerId = containerId;
    this.callbacks = callbacks;
    this.provider = "osm";
    this.layerKey = "standard";
    this.map = null;
    this.amap = null;
    this.osmLayers = null;
    this.activeOsmLayer = null;
    this.pickMarker = null;
    this.photoLayerGroup = null;
    this.trackPolyline = null;
    this.gpxPreviewPolyline = null;
    this.showTrack = true;
    this.pickedWgs = null;
    this.amapLoadedKey = "";
  }

  ensureInit(settings) {
    const hasAmap = Boolean(settings.aMapJsKey && settings.aMapSecurityJsCode);
    const desiredProvider = settings.mapProvider === "amap" && hasAmap ? "amap" : "osm";
    this.provider = desiredProvider;

    if (desiredProvider === "amap") {
      this.initAmap(settings);
    } else {
      this.initLeaflet();
    }
  }

  initLeaflet() {
    if (this.map && this.provider === "osm") {
      setTimeout(() => this.map.invalidateSize(), 60);
      return;
    }

    this.destroyCurrent();
    if (typeof window.L === "undefined") return;

    const L = window.L;
    this.map = L.map(this.containerId, { zoomControl: false }).setView([35.86, 104.19], 4);
    L.control.zoom({ position: "bottomright" }).addTo(this.map);
    this.osmLayers = {
      standard: L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", { maxZoom: 19, attribution: "&copy; OpenStreetMap" }),
      topo: L.tileLayer("https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", { maxZoom: 17, attribution: "&copy; OpenTopoMap" }),
      satellite: L.tileLayer("https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", { maxZoom: 19, attribution: "&copy; Esri" })
    };
    this.setLayer(this.layerKey);
    this.photoLayerGroup = L.layerGroup().addTo(this.map);

    this.map.on("click", (ev) => {
      this.setPickedPoint(ev.latlng.lat, ev.latlng.lng, "", true);
    });
  }

  async initAmap(settings) {
    if (this.amap && this.amapLoadedKey === settings.aMapJsKey) return;
    this.destroyCurrent();
    try {
      window._AMapSecurityConfig = { securityJsCode: settings.aMapSecurityJsCode };
      await loadScriptOnce(
        `https://webapi.amap.com/maps?v=2.0&key=${encodeURIComponent(settings.aMapJsKey)}&plugin=AMap.PlaceSearch,AMap.Geocoder,AMap.Scale`,
        "amap-sdk"
      );
      this.amapLoadedKey = settings.aMapJsKey;
      const gcjCenter = this.pickedWgs ? wgs84ToGcj02(this.pickedWgs.lat, this.pickedWgs.lon) : { lat: 39.9042, lon: 116.4074 };
      this.amap = new window.AMap.Map(this.containerId, {
        viewMode: "2D",
        zoom: this.pickedWgs ? 14 : 4,
        center: [gcjCenter.lon, gcjCenter.lat]
      });
      this.amap.on("click", (ev) => {
        const wgs = gcj02ToWgs84(ev.lnglat.getLat(), ev.lnglat.getLng());
        this.setPickedPoint(wgs.lat, wgs.lon, "", true);
      });
    } catch {
      this.provider = "osm";
      this.initLeaflet();
    }
  }

  destroyCurrent() {
    if (this.map) { this.map.remove(); this.map = null; }
    if (this.amap) { this.amap.destroy(); this.amap = null; }
    this.pickMarker = null;
    this.photoLayerGroup = null;
    this.trackPolyline = null;
    this.gpxPreviewPolyline = null;
  }

  setLayer(key) {
    this.layerKey = key;
    if (this.provider === "osm" && this.map && this.osmLayers) {
      const next = this.osmLayers[key] || this.osmLayers.standard;
      if (this.activeOsmLayer) this.map.removeLayer(this.activeOsmLayer);
      this.activeOsmLayer = next;
      this.activeOsmLayer.addTo(this.map);
    } else if (this.provider === "amap" && this.amap && window.AMap) {
      const AMap = window.AMap;
      if (key === "satellite") this.amap.setLayers([new AMap.TileLayer.Satellite()]);
      else if (key === "topo") this.amap.setLayers([new AMap.TileLayer.Satellite(), new AMap.TileLayer.RoadNet()]);
      else this.amap.setLayers([new AMap.TileLayer()]);
    }
  }

  setPickedPoint(lat, lon, label = "", reverseLookup = false, flyTo = false) {
    this.pickedWgs = { lat, lon, label };
    if (this.provider === "osm" && this.map && window.L) {
      const L = window.L;
      if (!this.pickMarker) {
        this.pickMarker = L.marker([lat, lon], { draggable: true }).addTo(this.map);
        this.pickMarker.on("dragend", () => {
          const ll = this.pickMarker.getLatLng();
          this.setPickedPoint(ll.lat, ll.lng, "", true);
        });
      } else {
        this.pickMarker.setLatLng([lat, lon]);
      }
      if (flyTo) this.map.setView([lat, lon], Math.max(this.map.getZoom(), 14));
    } else if (this.provider === "amap" && this.amap && window.AMap) {
      const gcj = wgs84ToGcj02(lat, lon);
      const ll = new window.AMap.LngLat(gcj.lon, gcj.lat);
      if (!this.pickMarker) {
        this.pickMarker = new window.AMap.Marker({ position: ll, draggable: true });
        this.amap.add(this.pickMarker);
        this.pickMarker.on("dragend", (ev) => {
          const w = gcj02ToWgs84(ev.lnglat.getLat(), ev.lnglat.getLng());
          this.setPickedPoint(w.lat, w.lon, "", true);
        });
      } else {
        this.pickMarker.setPosition(ll);
      }
      if (flyTo) this.amap.setZoomAndCenter(15, ll);
    }

    this.callbacks.onPointPicked?.({ lat, lon, label });
    if (reverseLookup && !label) this.reverseGeocode(lat, lon);
  }

  async reverseGeocode(lat, lon) {
    try {
      const resp = await fetch(
        `https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=${lat.toFixed(6)}&lon=${lon.toFixed(6)}`,
        { headers: { "Accept-Language": "zh-CN,en" } }
      );
      if (!resp.ok) return;
      const data = await resp.json();
      if (data && data.display_name && this.pickedWgs && Math.abs(this.pickedWgs.lat - lat) < 1e-4) {
        this.pickedWgs.label = data.name || data.display_name.split(",").slice(0, 2).join(", ");
        this.callbacks.onPointPicked?.({ lat, lon, label: this.pickedWgs.label });
      }
    } catch { /* silent offline fallback */ }
  }

  async search(keyword) {
    if (!keyword.trim()) return [];
    if (this.provider === "amap" && this.amap && window.AMap) {
      return new Promise((resolve) => {
        const ps = new window.AMap.PlaceSearch({ city: "全国", pageSize: 8 });
        ps.search(keyword, (status, res) => {
          if (status !== "complete" || !res?.poiList?.pois?.length) return resolve([]);
          resolve(res.poiList.pois.map((p) => {
            const w = gcj02ToWgs84(p.location.getLat(), p.location.getLng());
            return { name: p.name, sub: p.address || "", lat: w.lat, lon: w.lon };
          }));
        });
      });
    }

    const url = `https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&q=${encodeURIComponent(keyword)}`;
    const resp = await fetch(url, { headers: { "Accept-Language": "zh-CN,en" } });
    if (!resp.ok) return [];
    const items = await resp.json();
    return items.map((x) => ({
      name: x.name || x.display_name.split(",")[0],
      sub: x.display_name,
      lat: parseFloat(x.lat),
      lon: parseFloat(x.lon)
    }));
  }

  syncWorkspacePhotos(photos, focusedId) {
    if (this.provider !== "osm" || !this.map || !window.L || !this.photoLayerGroup) return;
    const L = window.L;
    this.photoLayerGroup.clearLayers();
    if (this.trackPolyline) { this.map.removeLayer(this.trackPolyline); this.trackPolyline = null; }

    const geotagged = photos
      .filter((p) => p.hasGps && Number.isFinite(p.latitude) && Number.isFinite(p.longitude))
      .sort((a, b) => (a.dateTaken || "").localeCompare(b.dateTaken || ""));

    const coords = [];
    for (const p of geotagged) {
      coords.push([p.latitude, p.longitude]);
      const isFocused = p.id === focusedId;
      const marker = L.circleMarker([p.latitude, p.longitude], {
        radius: isFocused ? 8 : 5.5,
        color: isFocused ? "#ffffff" : "#091629",
        weight: isFocused ? 2.5 : 1.5,
        fillColor: isFocused ? "#fbbf24" : "#38bdf8",
        fillOpacity: 0.92
      });
      marker.bindTooltip(`${p.fileName}${p.dateTaken ? " · " + p.dateTaken : ""}`);
      marker.on("click", () => this.callbacks.onPhotoPinClick?.(p.id));
      marker.addTo(this.photoLayerGroup);
    }

    if (this.showTrack && coords.length >= 2) {
      this.trackPolyline = L.polyline(coords, {
        color: "#8fd3f4",
        weight: 2.5,
        opacity: 0.72,
        dashArray: "6 5"
      }).addTo(this.map);
    }
  }

  fitToPhotos(photos) {
    const pts = photos.filter((p) => p.hasGps).map((p) => [p.latitude, p.longitude]);
    if (pts.length === 0) return;
    if (this.provider === "osm" && this.map && window.L) {
      this.map.fitBounds(window.L.latLngBounds(pts), { padding: [40, 40], maxZoom: 15 });
    }
  }

  setShowTrack(show, photos, focusedId) {
    this.showTrack = show;
    this.syncWorkspacePhotos(photos, focusedId);
  }

  pixelToLatLng(x, y) {
    if (this.provider === "osm" && this.map) {
      const ll = this.map.containerPointToLatLng([x, y]);
      return { lat: ll.lat, lon: ll.lng };
    }
    if (this.provider === "amap" && this.amap && window.AMap) {
      const ll = this.amap.containerToLngLat(new window.AMap.Pixel(x, y));
      return gcj02ToWgs84(ll.getLat(), ll.getLng());
    }
    return null;
  }
}

/* ---- WGS-84 / GCJ-02 Fast Client Transform for Map Preview ---- */
function outOfChina(lat, lon) {
  return lon < 72.004 || lon > 137.8347 || lat < 0.8293 || lat > 55.8271;
}

function deltaGcj(lat, lon) {
  const a = 6378245.0, ee = 0.00669342162296594323;
  const x = lon - 105.0, y = lat - 35.0;
  let dLat = -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.sqrt(Math.abs(x));
  dLat += (20 * Math.sin(6 * x * Math.PI) + 20 * Math.sin(2 * x * Math.PI)) * 2 / 3;
  dLat += (20 * Math.sin(y * Math.PI) + 40 * Math.sin(y / 3 * Math.PI)) * 2 / 3;
  dLat += (160 * Math.sin(y / 12 * Math.PI) + 320 * Math.sin(y * Math.PI / 30)) * 2 / 3;
  let dLon = 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.sqrt(Math.abs(x));
  dLon += (20 * Math.sin(6 * x * Math.PI) + 20 * Math.sin(2 * x * Math.PI)) * 2 / 3;
  dLon += (20 * Math.sin(x * Math.PI) + 40 * Math.sin(x / 3 * Math.PI)) * 2 / 3;
  dLon += (150 * Math.sin(x / 12 * Math.PI) + 300 * Math.sin(x / 30 * Math.PI)) * 2 / 3;
  const radLat = (lat / 180) * Math.PI;
  let magic = Math.sin(radLat);
  magic = 1 - ee * magic * magic;
  const sqrtMagic = Math.sqrt(magic);
  dLat = (dLat * 180) / (((a * (1 - ee)) / (magic * sqrtMagic)) * Math.PI);
  dLon = (dLon * 180) / ((a / sqrtMagic) * Math.cos(radLat) * Math.PI);
  return { dLat, dLon };
}

export function wgs84ToGcj02(lat, lon) {
  if (outOfChina(lat, lon)) return { lat, lon };
  const d = deltaGcj(lat, lon);
  return { lat: lat + d.dLat, lon: lon + d.dLon };
}

export function gcj02ToWgs84(lat, lon) {
  if (outOfChina(lat, lon)) return { lat, lon };
  const d = deltaGcj(lat, lon);
  return { lat: lat - d.dLat, lon: lon - d.dLon };
}

function loadScriptOnce(src, id) {
  return new Promise((resolve, reject) => {
    if (document.getElementById(id)) return resolve();
    const s = document.createElement("script");
    s.id = id;
    s.src = src;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error("Failed to load script"));
    document.head.appendChild(s);
  });
}
