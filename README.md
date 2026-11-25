# Photo Info Editor · Lumina Studio (v1.0.0)

新一代现代照片与视频元数据工作室（Hybrid .NET 8 + WebView2 + 常驻 ExifTool 引擎）。

## 核心特性

**美学与工作台体验**
- **6 套原创光影主题与 WebGL 着色器**：曜石水银 Obsidian / 蓝调潮汐 Bleu / 极光暮紫 Aurora / 琥珀暗房 Amber / 敦煌金沙 Dune / 宣纸绯樱 Sakura（浅色和纸模式）
- **纯净暗房模式**：一键静默背景动画，专注照片审片与标注
- **三视图切换**：虚拟化胶片灯箱墙（支持 5000+ 媒体流畅滚动与拖拽定位）/ 精密遥测表 / 空间足迹地图台
- **暗房看图 (Loupe)**：`Space` 或双击打开全屏看图，支持画廊白框预览与全标签检索

**性能与底层引擎**
- **ExifTool `-stay_open` 常驻守护进程**：彻底消除每次读写 500ms+ 冷启动开销，毫秒级批量读写与块级进度汇报
- **Windows Shell 硬件缩略图服务**：原生解码 JPG/HEIC/RAW/MP4/MOV 并自动处理 EXIF 旋转，二级内存 LRU + 磁盘缓存
- **原位直写撤销日志 (`Ctrl+Z`)**：直接修改原文件前自动记录元数据快照，支持误操作一键撤回

**GPS 与空间地图**
- **内嵌 OSM / 地形 / Esri 卫星地图 + 高德地图 2.0**：支持免 Key 全球地名检索、逆地理编码、照片轨迹连线与拖拽卡片到地图写点
- **GPX 轨迹时间插值匹配**：导入 `.gpx` 轨迹自动按拍摄时间线性插值写入 GPS，并支持导出 `.gpx` / `.geojson` / `.csv`
- **坐标系精准换算**：WGS-84 / GCJ-02 / BD-09 全格式解析与自动转换

**时间工坊 · 格式工具 · 隐私净空**
- **智能时间诊断 2.0**：自动识别相机文件名时间 (`IMG_YYYYMMDD_HHMMSS`) 与微信/导出毫秒时间戳 (`mmexport...`)，比对 EXIF 与文件时间
- **时区/偏移微调与序列递增**：支持 `±天/时/分/秒` 偏移实时预览、序列步进以及同步文件系统创建/修改时间
- **无损检测与批量转换**：`stackalloc` 零分配魔数识别（含 AVIF / DNG / CR2 / CR3 / NEF / ARW / RAF / 视频），支持质量与长边缩放控制
- **隐私净空器**：支持仅清除 GPS、社交分享净空（删 GPS/序列号/MakerNotes 并保留相机与色彩配置）及全量擦除

---

## 运行与打包

- **编译与测试**：`dotnet test PhotoLocationEditor.sln -c Release`
- **自包含发布**：`dotnet publish src\PhotoLocationEditor.App\PhotoLocationEditor.App.csproj -c Release -r win-x64 --self-contained true -o dist\publish`
- **无界面冒烟自检**：`PhotoInfoEditor.exe --smoke-test`

---

# English

Modern Hybrid .NET 8 + WebView2 desktop studio for photo & video metadata editing, geospatial tagging, time diagnostics, format conversion, and privacy scrubbing.

## Highlights
- **6 Curated Themes & Bespoke WebGL Shaders**: Obsidian, L'Heure Bleue, Aurora Veil, Liquid Amber, Dunhuang Dune, and Washi Sakura (light theme), plus **Pure Darkroom Mode**.
- **Persistent `ExifTool` Daemon (`-stay_open`)**: Eliminates per-command Perl startup latency; UTF-8 no-BOM piping supports CJK & emoji paths cleanly.
- **Native Shell Thumbnail Pipeline**: Hardware-accelerated thumbnails for JPEG/HEIC/RAW/Video with EXIF auto-orientation and 2-tier memory/disk cache.
- **Unified Tri-View Stage**: Virtualized Lightbox Grid, Telemetry Table, and Spatial Atlas (Leaflet OSM/Topo/Satellite with Nominatim search + AMap 2.0, GPX track matching, and footprint export).
- **Chronos Date Studio**: Filename & Unix-epoch timestamp recovery, timezone shift preview, sequence stepping, and OS file timestamp synchronization.
- **Undoable Direct Writes (`Ctrl+Z`)** & **3-Tier Privacy Scrubber**.
