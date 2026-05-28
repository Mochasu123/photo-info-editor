# Photo Location Editor

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
