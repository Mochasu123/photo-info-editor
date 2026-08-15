# Photo Info Editor

照片元数据编辑工具。原名 Photo Location Editor。

## 功能

**GPS 位置**
- 手动输入 + 地图选点（高德/OSM 可切换）+ 参考照片提取
- 支持 WGS-84 / GCJ-02 / BD-09 坐标系转换
- 多种格式输入（十进制度、度分秒、N/E 前后缀）
- 批量写入两种模式：输出到新目录 / 直接写入原文件（Backup 模式已移除）
- 写入支持 JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP
- MP4/MOV/M4V/3GP 支持实验性 QuickTime GPS 写入

**日期工具**
- DatePicker 选日期 + ▲▼ 调时间（HH:MM）
- 日期校对：智能比对 EXIF / 文件创建 / 文件修改时间，分类展示建议
- 手动写入 + 参考照片提取日期
- 日期写入同样遵守两种写入模式
- MP4/MOV/M4V/3GP 支持实验性 QuickTime 日期写入

**格式工具**
- 自动检测真实格式（magic bytes），异常后缀 ⚠ 警示 + 一键更正
- 图片转换：JPEG/PNG/GIF/BMP/TIFF 互转
- 支持识别 JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP/MP4/MOV/AVI/MKV/WMV/MTS/M2TS 等格式
- 合法别名后缀（如 .jpeg/.tif/.m2ts）不会被误判为异常

**界面**
- 三套主题：晨光 Light / 薄暮 Sepia / 暗夜 Dark
- 现代化设计系统：分段控件、导航侧栏、状态徽章、统一的圆角与间距
- 双语音：中文/English
- 大图预览 + 键盘翻页
- 表格列自由拖拽 + 双击自适应 + 键盘上下行切换
- 筛选栏 + 搜索 + 统计
- 偏好全持久化

---

## 安装

下载 `PhotoInfoEditor-0.3.0-win-x64.zip`，解压到任意目录，运行 `PhotoInfoEditor.exe`。

- Windows x64，无需安装 .NET 运行时

## 写入安全

- **直接写入原文件**：速度最快，不创建备份，建议写入前自行备份。

- **输出到新目录**：先复制到输出目录，只修改副本。
- MP4/MOV/M4V/3GP 会写入 QuickTime GPS/日期标签；AVI/MKV/WMV/MTS/M2TS 目前只读取元数据。
- 视频写入能否被手机相册识别取决于系统和容器标签规则，建议先用副本验证。

---

## 高德地图配置

打开 **Map 选点**，侧边栏配置 JS Key 和 Security JS Code。不配置时默认使用 OpenStreetMap。

---

## License

MIT

---

# English

Photo metadata editing tool. Formerly Photo Location Editor.

## Features

**GPS Location**
- Manual input + map picker (AMap/OSM switchable) + reference photo extraction
- WGS-84 / GCJ-02 / BD-09 coordinate conversion
- Multiple input formats (decimal, DMS, directional prefixes)
- Batch write: Copy to output directory / Direct write (backup mode removed)
- Metadata writing supports JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP.
- MP4/MOV/M4V/3GP support experimental QuickTime GPS writing.

**Date Tools**
- DatePicker + ▲▼ time adjust (HH:MM)
- Date check: intelligent comparison of EXIF / file creation / modification times
- Manual write + reference photo date extraction
- Date writes follow the same Copy / Direct write modes
- MP4/MOV/M4V/3GP support experimental QuickTime date writing.

**Format Tools**
- Magic bytes detection with ⚠ mismatch warning + one-click fix
- Image conversion: JPEG/PNG/GIF/BMP/TIFF
- Format detection for JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP/MP4/MOV/AVI/MKV/WMV/MTS/M2TS
- Valid extension aliases such as .jpeg/.tif/.m2ts are not flagged as mismatches

**UI**
- 3 themes: Light / Sepia / Dark
- Modern design system: segmented controls, navigation sidebar, status badges, unified corner radius and spacing
- Bilingual: Chinese / English
- Full image preview + keyboard navigation
- Column drag-reorder + double-click auto-fit + keyboard row nav
- Filter bar + search + statistics
- Full preference persistence

## Installation

Download `PhotoInfoEditor-0.3.0-win-x64.zip`, extract, run `PhotoInfoEditor.exe`.

- Windows x64, self-contained, no .NET runtime installation needed

## Write Safety

- **Direct write**: fastest, no backup is created. Make your own backup first.

- **Copy to output directory**: copies files to the selected output directory and edits only the copies.
- MP4/MOV/M4V/3GP write QuickTime GPS/date tags. AVI/MKV/WMV/MTS/M2TS remain read-only for now.
- Whether video GPS/date metadata is recognized depends on the phone gallery and container tag rules. Test on copies first.

## License

MIT
