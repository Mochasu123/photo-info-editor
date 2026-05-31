# Photo Info Editor

照片元数据编辑工具。原名 Photo Location Editor。

## 功能

**GPS 位置**
- 手动输入 + 地图选点（高德/OSM 可切换）+ 参考照片提取
- 支持 WGS-84 / GCJ-02 / BD-09 坐标系转换
- 多种格式输入（十进制度、度分秒、N/E 前后缀）
- 批量写入三种模式：直接写入 / 原地备份 / 输出到新目录

**日期工具**
- DatePicker 选日期 + ▲▼ 调时间（HH:MM）
- 日期校对：智能比对 EXIF / 文件创建 / 文件修改时间，分类展示建议
- 手动写入 + 参考照片提取日期

**格式工具**
- 自动检测真实格式（magic bytes），异常后缀 ⚠ 警示 + 一键更正
- 图片转换：JPEG/PNG/GIF/BMP/TIFF 互转（GDI+），HEIC/WebP（ExifTool）
- 视频支持：MP4/MOV/AVI/MKV/MTS 等格式读取元数据

**界面**
- 三套主题：晨光 Light / 薄暮 Sepia / 暗夜 Dark
- 四色功能模块卡片（蓝/橙/绿/紫）
- 双语音：中文/English
- 大图预览 + 键盘翻页
- 表格列自由拖拽 + 双击自适应 + 键盘上下行切换
- 筛选栏 + 搜索 + 统计
- 偏好全持久化

---

## 安装

下载 `PhotoInfoEditor-0.2.0-win-x64.zip`，解压到任意目录，运行 `PhotoInfoEditor.App.exe`。

- Windows x64，无需安装 .NET 运行时

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
- Batch write: Direct / Backup / Copy to output directory

**Date Tools**
- DatePicker + ▲▼ time adjust (HH:MM)
- Date check: intelligent comparison of EXIF / file creation / modification times
- Manual write + reference photo date extraction

**Format Tools**
- Magic bytes detection with ⚠ mismatch warning + one-click fix
- Image conversion: JPEG/PNG/GIF/BMP/TIFF via GDI+, HEIC/WebP via ExifTool
- Video support: MP4/MOV/AVI/MKV/MTS metadata reading

**UI**
- 3 themes: Light / Sepia / Dark
- 4-color card zones (Blue/Orange/Green/Purple)
- Bilingual: Chinese / English
- Full image preview + keyboard navigation
- Column drag-reorder + double-click auto-fit + keyboard row nav
- Filter bar + search + statistics
- Full preference persistence

## Installation

Download `PhotoInfoEditor-0.2.0-win-x64.zip`, extract, run `PhotoInfoEditor.App.exe`.

- Windows x64, self-contained, no .NET runtime installation needed

## License

MIT
