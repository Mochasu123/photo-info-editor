# Photo Location Editor

一款 Windows 照片 GPS 编辑工具，用于手动或批量写入、修正标准 EXIF GPS 位置信息。

## 项目缘起

这个工具来自一个真实的旅行照片管理需求：我出门时会同时使用手机和相机拍照，例如手机是华为 Mate 系列，相机是 Fujifilm X-M5。相机照片导入电脑、再传到手机后，照片本身通常没有 GPS 位置信息，手机图库就无法像手机拍摄的照片那样按地图位置统一查看和管理。

因此，这个软件的目标是在照片传到手机前，先把可靠的标准 EXIF GPS 信息写入照片文件。这样无论后续传到华为手机、iPhone，还是其他图库应用，只要它们读取图片本身的 GPS metadata，就能正确显示地图位置。

## 功能

- 通过文件选择、文件夹选择、拖拽导入照片。
- 读取已有 EXIF GPS、相机型号、拍摄时间。
- 支持多种格式手动输入 GPS 坐标。
- 在内置地图中选点或修改已有位置。
- 支持批量写入 GPS metadata。
- 写入方式：
  - 输出到新目录。
  - 原地写入并创建 Backup。
  - 直接写入原文件。
- 支持 WGS-84、GCJ-02、BD-09 坐标转换。
- 未配置高德 Key 时，默认使用 OpenStreetMap 免费兜底。
- 可在 Map Picker 中配置自己的高德 JS Key 和 Security JS Code。

## 高德地图配置

打开 **Map Picker**，在右侧栏展开 **高德配置 / AMap Key**。

- 不配置高德 Key 时，软件使用 OpenStreetMap 作为免费备用地图。
- 配置自己的高德 JS Key 和 Security JS Code 后，可以切换到高德地图，并使用高德 POI 搜索、地址解析和图层。
- 配置会保存在本机：`%AppData%\PhotoLocationEditor\settings.json`。

## 安装包

Release 版本是 Windows x64 self-contained 安装包，包含运行所需的 .NET 组件和用于读写 metadata 的 ExifTool。

## 第三方组件

- ExifTool by Phil Harvey：用于读取和写入照片 metadata。
- Microsoft WebView2：用于内置地图显示。
- OpenStreetMap 和 AMap：根据用户配置作为地图服务来源。

## License

本项目使用 MIT License。

---

# English

A Windows photo GPS editor for manually adding or correcting standard EXIF GPS metadata.

## Why this exists

This app was built for a real travel workflow: photos taken on a Fujifilm X-M5 are often imported to a phone for storage and browsing, but camera photos do not contain phone GPS metadata by default. Phone gallery apps can group and display map locations only when location data exists in the image itself. The tool therefore focuses on adding reliable EXIF GPS data before photos are transferred to phones, including Huawei and iPhone workflows.

## Features

- Import photos by file picker, folder picker, or drag and drop.
- Read existing EXIF GPS, camera model, and shooting time.
- Manually enter GPS in multiple formats.
- Pick or edit locations on an embedded map.
- Batch write GPS metadata.
- Write modes:
  - Copy to output directory.
  - Write in place with Backup.
  - Direct write in place.
- Coordinate conversion for WGS-84, GCJ-02, and BD-09.
- OpenStreetMap fallback when no AMap key is configured.
- Optional AMap JS Key and Security JS Code configuration inside Map Picker.

## AMap configuration

Open Map Picker and expand **AMap Key** in the sidebar.

- If no AMap key is configured, the app uses OpenStreetMap as a free fallback.
- If you configure your own AMap JS Key and Security JS Code, AMap search and AMap layers become available.
- Settings are saved locally under `%AppData%\PhotoLocationEditor\settings.json`.

## Packaging

Release builds are self-contained Windows x64 packages and include ExifTool for metadata writing.

## Third-party components

- ExifTool by Phil Harvey is used for metadata reading and writing.
- Microsoft WebView2 is used for embedded map display.
- OpenStreetMap and AMap are used as map providers depending on user configuration.

## License

This project is released under the MIT License.
