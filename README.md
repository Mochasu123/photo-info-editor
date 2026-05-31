# Photo Info Editor

照片元数据编辑工具。原名 Photo Location Editor。

## v0.2.0 更新 (2026-05-31)

### 新增功能
- **日期工具**：支持 DatePicker 选日期、自定义输入时间、以文件创建时间写入、以较早时间纠正
- **参考照片弹窗**：Tab 拖拽重排，已导入照片/本地文件双来源
- **图片格式检测**：magic bytes 12字节自动检测7种格式，异常后缀 ⚠ 警示 + 一键更正
- **图片格式转换**：JPEG/PNG/GIF/BMP/TIFF 互转（GDI+），HEIC/WebP 转换（ExifTool），复用三种写入模式
- **三套主题**：晨光 Light / 薄暮 Sepia / 暗夜 Dark，DynamicResource 实时切换
- **大图预览**：点击缩略图弹窗，支持键盘翻页，同步表格高亮
- **双语音界面**：中文/English 实时切换
- **应用图标**：自定义 icon

### 优化
- 默认写入模式改为直接写入，大幅提速
- ExifTool 调用合并 + 分组批量处理，避免逐张调用
- UI 重构：Fluent 风格圆角卡片，四色功能模块分区，折叠面板
- 表格列自由拖拽重排 + 双击自适应 + 键盘上下切换行
- 筛选栏 + 搜索 + 统计栏
- 偏好持久化：主题/语言/写入模式/输出目录/窗口位置/列顺序/Tab顺序

### 修复
- 首次启动目录框可见性
- 深色主题按钮可读性
- ComboBox 文字重叠
- 鼠标点击行切换卡顿

---

## 安装

下载 `PhotoInfoEditor-0.2.0-win-x64.zip`，解压到任意目录，运行 `PhotoLocationEditor.App.exe`。

- 内置 ExifTool，无需额外安装
- Windows x64 self-contained，无需安装 .NET 运行时

## 高德地图配置

打开 **Map 选点**，在侧边栏配置 JS Key 和 Security JS Code。不配置时默认使用 OpenStreetMap。

---

# English

Photo metadata editing tool. Formerly Photo Location Editor.

## v0.2.0 Changes

### New Features
- **Date Tools**: DatePicker, custom time input, set to file creation time, correct to earlier time
- **Reference Photo Dialog**: Drag-reorder tabs, imported photos + local file dual sources
- **Format Detection**: 12-byte magic header detects 7 formats, ⚠ mismatch warning + one-click fix
- **Format Conversion**: JPEG/PNG/GIF/BMP/TIFF via GDI+, HEIC/WebP via ExifTool, 3 write modes
- **3 Themes**: Light / Sepia / Dark, switchable at runtime
- **Fullscreen Preview**: Click thumbnail to open, keyboard navigation, syncs table highlight
- **Bilingual UI**: Chinese / English
- **App Icon**: Custom icon

### Improvements
- Default write mode: Direct In Place
- Merged + grouped ExifTool calls for batch speed
- Fluent-style rounded card UI with 4-color functional zones
- Column drag-reorder + double-click auto-fit + keyboard row navigation
- Filter bar + search + statistics
- Preference persistence across sessions

## Installation

Download `PhotoInfoEditor-0.2.0-win-x64.zip`, extract, run `PhotoLocationEditor.App.exe`.

## License

MIT
