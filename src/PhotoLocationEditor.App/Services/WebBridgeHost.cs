using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public sealed class WebBridgeHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Window _window;
    private readonly CoreWebView2 _webView;
    private readonly ExifToolService _exifTool;
    private readonly ThumbnailService _thumbnails;
    private readonly ImageConversionService _converter;
    private readonly AppSettingsService _settingsService;
    private readonly MetadataUndoService _undoService = new();
    private readonly List<PhotoItem> _photos = [];
    private readonly object _photosLock = new();
    private readonly AppSettings _settings;
    private readonly Action<string> _onThemeChanged;

    private CancellationTokenSource? _busyCts;
    private bool _isBusy;

    public WebBridgeHost(
        Window window,
        CoreWebView2 webView,
        ExifToolService exifTool,
        ThumbnailService thumbnails,
        AppSettingsService settingsService,
        AppSettings settings,
        Action<string> onThemeChanged)
    {
        _window = window;
        _webView = webView;
        _exifTool = exifTool;
        _thumbnails = thumbnails;
        _converter = new ImageConversionService(exifTool);
        _settingsService = settingsService;
        _settings = settings;
        _onThemeChanged = onThemeChanged;
    }

    public PhotoItem? FindPhotoById(string id)
    {
        lock (_photosLock)
            return _photos.FirstOrDefault(p => p.Id == id);
    }

    public async Task HandleMessageAsync(string messageJson, IReadOnlyList<object>? additionalObjects)
    {
        using var doc = JsonDocument.Parse(messageJson);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
        var reqId = root.TryGetProperty("reqId", out var rp) ? rp.GetString() : null;
        var payload = root.TryGetProperty("payload", out var pp) ? pp : default;

        try
        {
            object? result = type switch
            {
                "init" => await HandleInitAsync(),
                "windowControl" => HandleWindowControl(payload),
                "saveSettings" => HandleSaveSettings(payload),
                "addFiles" => await HandleAddFilesAsync(),
                "addFolder" => await HandleAddFolderAsync(payload),
                "browseOutputDir" => HandleBrowseOutputDir(),
                "dropFiles" => await HandleDropFilesAsync(payload, additionalObjects),
                "removePhotos" => HandleRemovePhotos(payload),
                "clearPhotos" => HandleClearPhotos(),
                "parseGps" => HandleParseGps(payload),
                "writeGps" => await HandleWriteGpsAsync(payload),
                "clearMetadata" => await HandleClearMetadataAsync(payload),
                "diagnoseDates" => await HandleDiagnoseDatesAsync(payload),
                "applyDateDiagnosis" => await HandleApplyDateDiagnosisAsync(payload),
                "writeDate" => await HandleWriteDateAsync(payload),
                "fixExtensions" => await HandleFixExtensionsAsync(payload),
                "convertFormat" => await HandleConvertFormatAsync(payload),
                "writeCameraInfo" => await HandleWriteCameraInfoAsync(payload),
                "pickReferenceFile" => await HandlePickReferenceFileAsync(),
                "importGpx" => await HandleImportGpxAsync(payload),
                "applyGpxMatches" => await HandleApplyGpxMatchesAsync(payload),
                "exportFootprint" => HandleExportFootprint(payload),
                "readAllTags" => await HandleReadAllTagsAsync(payload),
                "undo" => await HandleUndoAsync(),
                "revealInExplorer" => HandleRevealInExplorer(payload),
                "cancelBusy" => HandleCancelBusy(),
                "openExternalUrl" => HandleOpenExternalUrl(payload),
                _ => throw new InvalidOperationException($"Unknown command: {type}")
            };

            if (!string.IsNullOrEmpty(reqId))
                PostResponse(reqId, ok: true, result, error: null);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrEmpty(reqId))
                PostResponse(reqId, ok: false, null, "Operation canceled.");
            EmitProgress(false, "Operation canceled", null);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(reqId))
                PostResponse(reqId, ok: false, null, ex.Message);
            EmitProgress(false, ex.Message, null);
        }
    }

    public async Task AddPathsAsync(IEnumerable<string> rawPaths)
    {
        var paths = rawPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && ExifToolService.IsSupportedImage(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PhotoItem[] added;
        lock (_photosLock)
        {
            var existing = _photos.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            added = paths.Where(p => existing.Add(p)).Select(p => new PhotoItem(p)).ToArray();
            _photos.AddRange(added);
        }

        if (added.Length == 0)
        {
            EmitToast("info", _settings.LastLanguage == "en" ? "No new supported files added." : "没有新增支持的媒体文件。");
            return;
        }

        // Push items immediately so skeleton cards show up right away
        EmitWorkspaceSync();

        using var cts = BeginBusy(out var token, _settings.LastLanguage == "en"
            ? $"Reading metadata ({added.Length})..."
            : $"正在读取 {added.Length} 项元数据...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            await _exifTool.ReadMetadataAsync(added, progress, token);

            await Task.Run(() =>
            {
                foreach (var photo in added)
                {
                    photo.SetDetectedFormat(FormatDetector.Detect(photo.Path));
                    try
                    {
                        var fi = new FileInfo(photo.Path);
                        photo.FileSizeBytes = fi.Length;
                        photo.SetFileCreationTime(fi.CreationTime);
                        photo.FileModificationTime = fi.LastWriteTime;
                        photo.RecomputeSolar();
                    }
                    catch { }
                }
            }, token);

            EmitWorkspaceSync();
            EmitToast("success", _settings.LastLanguage == "en"
                ? $"Imported {added.Length} files"
                : $"已导入 {added.Length} 个文件");
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleInitAsync()
    {
        _ = _exifTool.WarmUpAsync();
        await Task.CompletedTask;
        return new
        {
            appVersion = "1.0.0",
            exifToolReady = File.Exists(ExifToolService.DefaultPath),
            settings = _settings,
            photos = SnapshotPhotos(),
            undo = BuildUndoDto(),
            windowMaximized = _window.WindowState == WindowState.Maximized
        };
    }

    private object HandleWindowControl(JsonElement payload)
    {
        var action = payload.GetStringProp("action");
        switch (action)
        {
            case "drag":
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                    _window.DragMove();
                break;
            case "minimize":
                _window.WindowState = WindowState.Minimized;
                break;
            case "maximize":
                _window.WindowState = _window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                break;
            case "close":
                _window.Close();
                break;
        }
        return new { maximized = _window.WindowState == WindowState.Maximized };
    }

    private static string FormatDms(GpsCoordinate c)
    {
        static string Part(double val, string pos, string neg)
        {
            var abs = Math.Abs(val);
            var deg = (int)Math.Floor(abs);
            var minFull = (abs - deg) * 60.0;
            var min = (int)Math.Floor(minFull);
            var sec = (minFull - min) * 60.0;
            return string.Create(CultureInfo.InvariantCulture, $"{deg}°{min:00}'{sec:00.00}\"{(val >= 0 ? pos : neg)}");
        }
        return $"{Part(c.Latitude, "N", "S")} {Part(c.Longitude, "E", "W")}";
    }

    private object HandleSaveSettings(JsonElement payload)
    {
        if (payload.TryGetProperty("theme", out var th) && th.ValueKind == JsonValueKind.String)
        {
            _settings.Theme = th.GetString() ?? "obsidian";
            _onThemeChanged(_settings.Theme);
        }
        if (payload.TryGetProperty("lastLanguage", out var lang) && lang.ValueKind == JsonValueKind.String)
            _settings.LastLanguage = lang.GetString() ?? "zh";
        if (payload.TryGetProperty("lastWriteMode", out var wm) && wm.TryGetInt32(out var wmv))
            _settings.LastWriteMode = wmv;
        if (payload.TryGetProperty("lastOutputDirectory", out var od) && od.ValueKind == JsonValueKind.String)
            _settings.LastOutputDirectory = od.GetString() ?? "";
        if (payload.TryGetProperty("pureDarkroomMode", out var pd) && pd.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.PureDarkroomMode = pd.GetBoolean();
        if (payload.TryGetProperty("viewMode", out var vm) && vm.ValueKind == JsonValueKind.String)
            _settings.ViewMode = vm.GetString() ?? "grid";
        if (payload.TryGetProperty("mapProvider", out var mp) && mp.ValueKind == JsonValueKind.String)
            _settings.MapProvider = mp.GetString() ?? "osm";
        if (payload.TryGetProperty("smartSkipConfirm", out var sc) && sc.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.SmartSkipConfirm = sc.GetBoolean();
        if (payload.TryGetProperty("syncFileTimestamps", out var sf) && sf.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.SyncFileTimestamps = sf.GetBoolean();
        if (payload.TryGetProperty("jpegQuality", out var jq) && jq.TryGetInt32(out var jqv))
            _settings.JpegQuality = Math.Clamp(jqv, 40, 100);
        if (payload.TryGetProperty("thumbSize", out var ts) && ts.TryGetInt32(out var tsv))
            _settings.ThumbSize = Math.Clamp(tsv, 150, 380);
        if (payload.TryGetProperty("aMapJsKey", out var ak) && ak.ValueKind == JsonValueKind.String)
            _settings.AMapJsKey = ak.GetString() ?? "";
        if (payload.TryGetProperty("aMapSecurityJsCode", out var asc) && asc.ValueKind == JsonValueKind.String)
            _settings.AMapSecurityJsCode = asc.GetString() ?? "";
        if (payload.TryGetProperty("savedLocations", out var sl) && sl.ValueKind == JsonValueKind.Array)
            _settings.SavedLocations = JsonSerializer.Deserialize<List<SavedLocationPin>>(sl.GetRawText(), JsonOptions) ?? [];
        if (payload.TryGetProperty("recentLocations", out var rl) && rl.ValueKind == JsonValueKind.Array)
            _settings.RecentLocations = JsonSerializer.Deserialize<List<SavedLocationPin>>(rl.GetRawText(), JsonOptions) ?? [];

        _settingsService.Save(_settings);
        return new { saved = true };
    }

    private async Task<object> HandleAddFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.jpg;*.jpeg;*.heic;*.heif;*.hif;*.png;*.webp;*.avif;*.dng;*.cr2;*.cr3;*.nef;*.arw;*.raf;*.mp4;*.mov;*.avi;*.mkv;*.3gp;*.m4v;*.wmv;*.mts;*.m2ts|All files|*.*"
        };
        if (dialog.ShowDialog(_window) == true)
        {
            await AddPathsAsync(dialog.FileNames);
            return new { count = dialog.FileNames.Length };
        }
        return new { count = 0 };
    }

    private async Task<object> HandleAddFolderAsync(JsonElement payload)
    {
        var recursive = payload.GetBoolProp("includeSubfolders");
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _settings.LastLanguage == "en" ? "Choose Photo Folder" : "选择照片文件夹"
        };
        if (dialog.ShowDialog(_window) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return new { count = 0 };

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = await Task.Run(() =>
            Directory.EnumerateFiles(dialog.FolderName, "*", option)
                .Where(ExifToolService.IsSupportedImage)
                .ToArray());
        await AddPathsAsync(files);
        return new { count = files.Length };
    }

    private object HandleBrowseOutputDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _settings.LastLanguage == "en" ? "Choose Output Directory" : "选择安全副本输出目录"
        };
        if (dialog.ShowDialog(_window) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _settings.LastOutputDirectory = dialog.FolderName;
            _settingsService.Save(_settings);
            return new { path = dialog.FolderName };
        }
        return new { path = (string?)null };
    }

    private async Task<object> HandleDropFilesAsync(JsonElement payload, IReadOnlyList<object>? additionalObjects)
    {
        var recursive = payload.GetBoolProp("includeSubfolders");
        var rawPaths = new List<string>();
        if (additionalObjects is not null)
        {
            foreach (var obj in additionalObjects)
            {
                if (obj is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path))
                    rawPaths.Add(file.Path);
            }
        }

        if (rawPaths.Count == 0 && payload.TryGetProperty("paths", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in pArr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s)
                    rawPaths.Add(s);
        }

        var expanded = await Task.Run(() => ExpandPaths(rawPaths, recursive).ToArray());
        await AddPathsAsync(expanded);
        return new { count = expanded.Length };
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> inputs, bool recursive)
    {
        foreach (var path in inputs)
        {
            if (File.Exists(path) && ExifToolService.IsSupportedImage(path))
                yield return path;
            else if (Directory.Exists(path))
            {
                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(path, "*", opt).Where(ExifToolService.IsSupportedImage))
                    yield return f;
            }
        }
    }

    private object HandleRemovePhotos(JsonElement payload)
    {
        var ids = payload.GetStringArrayProp("ids").ToHashSet(StringComparer.Ordinal);
        lock (_photosLock)
            _photos.RemoveAll(p => ids.Contains(p.Id));
        EmitWorkspaceSync();
        return new { removed = ids.Count };
    }

    private object HandleClearPhotos()
    {
        lock (_photosLock)
            _photos.Clear();
        _thumbnails.ClearMemoryCache();
        _undoService.Clear();
        EmitWorkspaceSync();
        return new { cleared = true };
    }

    private object HandleParseGps(JsonElement payload)
    {
        var input = payload.GetStringProp("input") ?? "";
        var sysStr = payload.GetStringProp("system") ?? "wgs84";
        var sys = sysStr switch
        {
            "gcj02" => CoordinateSystemKind.Gcj02,
            "bd09" => CoordinateSystemKind.Bd09,
            _ => CoordinateSystemKind.Wgs84
        };

        if (!GpsParser.TryParse(input, out var parsed, out var error))
            return new { ok = false, error };

        var wgs = CoordinateTransform.ToWgs84(parsed, sys);
        var gcj = CoordinateTransform.Wgs84ToGcj02(wgs);
        return new
        {
            ok = true,
            wgs84 = new
            {
                lat = Math.Round(wgs.Latitude, 6),
                lon = Math.Round(wgs.Longitude, 6),
                alt = wgs.Altitude,
                display = wgs.Display,
                latRef = wgs.LatitudeRef,
                lonRef = wgs.LongitudeRef,
                dms = FormatDms(wgs)
            },
            gcj02 = new
            {
                lat = Math.Round(gcj.Latitude, 6),
                lon = Math.Round(gcj.Longitude, 6)
            }
        };
    }

    private async Task<object> HandleWriteGpsAsync(JsonElement payload)
    {
        var targets = ResolvePhotosByIds(payload.GetStringArrayProp("ids"));
        if (targets.Length == 0)
            throw new InvalidOperationException("No target photos selected.");

        var lat = payload.GetDoubleProp("lat") ?? throw new InvalidOperationException("Invalid latitude.");
        var lon = payload.GetDoubleProp("lon") ?? throw new InvalidOperationException("Invalid longitude.");
        var alt = payload.GetDoubleProp("alt");
        var mode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");
        var coord = new GpsCoordinate(lat, lon, alt);

        if (mode == WriteMode.DirectInPlace)
            PushUndo($"GPS → {coord.Display} ({targets.Length})", targets);

        using var cts = BeginBusy(out var token, $"Writing GPS ({targets.Length})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var result = await _exifTool.WriteGpsAsync(targets, coord, mode, outDir, progress, token);
            if (mode == WriteMode.DirectInPlace)
                await _exifTool.ReadMetadataAsync(targets, progress, token);

            foreach (var p in targets)
                p.Status = mode == WriteMode.DirectInPlace ? "GPS Written" : "GPS Copied";

            AddRecentLocation(lat, lon, alt, payload.GetStringProp("placeName"));
            EmitWorkspaceSync();
            return new { written = result.Targets.Count, skipped = result.SkippedFileNames };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleClearMetadataAsync(JsonElement payload)
    {
        var targets = ResolvePhotosByIds(payload.GetStringArrayProp("ids"));
        if (targets.Length == 0)
            throw new InvalidOperationException("No target photos selected.");

        var stripMode = payload.GetStringProp("stripMode") switch
        {
            "privacyKeepExif" => PrivacyStripMode.PrivacyCleanKeepExif,
            "privacyFull" => PrivacyStripMode.PrivacyCleanFull,
            _ => PrivacyStripMode.GpsOnly
        };
        var mode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");

        if (mode == WriteMode.DirectInPlace && stripMode != PrivacyStripMode.PrivacyCleanFull)
            PushUndo($"Clean {stripMode} ({targets.Length})", targets);

        using var cts = BeginBusy(out var token, $"Cleaning metadata ({targets.Length})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var result = await _exifTool.ClearMetadataAsync(targets, stripMode, mode, outDir, progress, token);
            foreach (var p in targets)
                p.Status = "Cleaned";
            EmitWorkspaceSync();
            return new { cleaned = result.Targets.Count, skipped = result.SkippedFileNames };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleDiagnoseDatesAsync(JsonElement payload)
    {
        var ids = payload.GetStringArrayProp("ids");
        var targets = ids.Length > 0 ? ResolvePhotosByIds(ids) : SnapshotPhotosRaw();
        var items = await Task.Run(() => DateIntelligenceService.Diagnose(targets));
        return new { items };
    }

    private async Task<object> HandleApplyDateDiagnosisAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Missing diagnosis items.");

        var pairs = new List<(PhotoItem Photo, string Date)>();
        foreach (var el in arr.EnumerateArray())
        {
            var id = el.GetStringProp("photoId");
            var date = el.GetStringProp("date");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(date)) continue;
            var p = FindPhotoById(id);
            if (p is not null)
                pairs.Add((p, date));
        }
        if (pairs.Count == 0)
            return new { written = 0, skipped = Array.Empty<string>() };

        var mode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");
        var syncFiles = payload.GetBoolProp("syncFileTimes");

        if (mode == WriteMode.DirectInPlace)
            PushUndo($"Date Diagnose Fix ({pairs.Count})", pairs.Select(x => x.Photo).ToArray());

        using var cts = BeginBusy(out var token, $"Fixing dates ({pairs.Count})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var result = await _exifTool.WriteDateBatchAsync(pairs, progress, mode, outDir, syncFiles, token);
            if (mode == WriteMode.DirectInPlace)
                await _exifTool.ReadMetadataAsync(pairs.Select(x => x.Photo).ToArray(), progress, token);
            EmitWorkspaceSync();
            return new { written = result.Targets.Count, skipped = result.SkippedFileNames };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleWriteDateAsync(JsonElement payload)
    {
        var targets = ResolvePhotosByIds(payload.GetStringArrayProp("ids"));
        if (targets.Length == 0)
            throw new InvalidOperationException("No target photos selected.");

        var subMode = payload.GetStringProp("mode") ?? "exact";
        var writeMode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");
        var syncFiles = payload.GetBoolProp("syncFileTimes");

        var batch = new List<(PhotoItem Photo, string Date)>();
        if (subMode == "shift")
        {
            var shiftSeconds = payload.GetLongProp("shiftSeconds") ?? 0;
            var delta = TimeSpan.FromSeconds(shiftSeconds);
            foreach (var p in targets)
            {
                var shifted = DateIntelligenceService.ShiftExifTime(p.DateTaken, delta);
                if (shifted is not null)
                    batch.Add((p, shifted));
            }
            if (batch.Count == 0)
                throw new InvalidOperationException("Selected photos have no existing EXIF date to shift.");
        }
        else if (subMode == "sequence")
        {
            var startStr = payload.GetStringProp("dateStr") ?? "";
            if (!DateIntelligenceService.TryParseExifDateTime(startStr, out var startDt))
                throw new InvalidOperationException("Invalid start date/time format.");
            var stepSec = payload.GetIntProp("sequenceStepSeconds") ?? 1;
            var ordered = targets.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            batch.AddRange(DateIntelligenceService.GenerateSequence(ordered, startDt, stepSec));
        }
        else
        {
            var dtStr = payload.GetStringProp("dateStr") ?? "";
            if (!DateIntelligenceService.TryParseExifDateTime(dtStr, out var dt))
                throw new InvalidOperationException("Invalid date/time format.");
            var normalized = DateIntelligenceService.FormatExifDateTime(dt);
            foreach (var p in targets)
                batch.Add((p, normalized));
        }

        if (writeMode == WriteMode.DirectInPlace)
            PushUndo($"Write Date ({batch.Count})", batch.Select(x => x.Photo).ToArray());

        using var cts = BeginBusy(out var token, $"Writing dates ({batch.Count})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var result = await _exifTool.WriteDateBatchAsync(batch, progress, writeMode, outDir, syncFiles, token);
            if (writeMode == WriteMode.DirectInPlace)
                await _exifTool.ReadMetadataAsync(batch.Select(x => x.Photo).ToArray(), progress, token);
            EmitWorkspaceSync();
            return new { written = result.Targets.Count, skipped = result.SkippedFileNames };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleFixExtensionsAsync(JsonElement payload)
    {
        var targets = ResolvePhotosByIds(payload.GetStringArrayProp("ids"))
            .Where(p => p.IsExtensionMismatched)
            .ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("No extension-mismatched files selected.");

        var snapshots = new List<MetadataSnapshot>();
        var renamed = 0;
        var skipped = 0;
        var updated = new List<PhotoItem>();

        using var cts = BeginBusy(out var token, $"Fixing extensions ({targets.Length})...");
        try
        {
            foreach (var photo in targets)
            {
                token.ThrowIfCancellationRequested();
                var correctExt = FormatDetector.GetStandardExtension(photo.DetectedFormat);
                var newPath = Path.ChangeExtension(photo.Path, correctExt.TrimStart('.'));
                if (string.Equals(photo.Path, newPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(newPath))
                {
                    skipped++;
                    photo.Status = "Target exists, skipped";
                    continue;
                }

                var oldPath = photo.Path;
                File.Move(oldPath, newPath);
                photo.UpdatePath(newPath);
                photo.SetDetectedFormat(FormatDetector.Detect(newPath));
                photo.Status = "Renamed";
                snapshots.Add(new MetadataSnapshot(newPath, oldPath, photo.Latitude, photo.Longitude, photo.Altitude, photo.DateTaken, photo.CameraMake, photo.CameraModel, photo.LensModel, photo.Artist, photo.Copyright));
                updated.Add(photo);
                renamed++;
            }

            if (snapshots.Count > 0)
                _undoService.Push($"Fix Extensions ({snapshots.Count})", snapshots);

            if (updated.Count > 0)
                await _exifTool.ReadMetadataAsync(updated, null, token);

            EmitWorkspaceSync();
            return new { renamed, skipped };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleConvertFormatAsync(JsonElement payload)
    {
        var targets = ResolvePhotosByIds(payload.GetStringArrayProp("ids"));
        if (targets.Length == 0)
            throw new InvalidOperationException("No photos selected.");

        var format = payload.GetStringProp("targetFormat") switch
        {
            "png" => ImageFormat.Png,
            "bmp" => ImageFormat.Bmp,
            "gif" => ImageFormat.Gif,
            "tiff" => ImageFormat.Tiff,
            _ => ImageFormat.Jpeg
        };
        var mode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");
        var quality = Math.Clamp(payload.GetIntProp("quality") ?? 92, 40, 100);
        var maxDim = payload.GetIntProp("maxDimension");
        if (maxDim is <= 0) maxDim = null;

        using var cts = BeginBusy(out var token, $"Converting ({targets.Length})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var options = new ConversionOptions(format, mode, outDir, quality, maxDim);
            var outputs = await _converter.ConvertAsync(targets, options, progress, token);
            _thumbnails.ClearMemoryCache();
            EmitWorkspaceSync();
            return new { converted = outputs.Count };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandleWriteCameraInfoAsync(JsonElement payload)
    {
        var targets = ResolvePhotosByIds(payload.GetStringArrayProp("ids"));
        if (targets.Length == 0)
            throw new InvalidOperationException("No photos selected.");

        var mode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");
        var edit = new CameraInfoEdit(
            payload.GetNullableStringProp("make"),
            payload.GetNullableStringProp("model"),
            payload.GetNullableStringProp("lensModel"),
            payload.GetNullableStringProp("artist"),
            payload.GetNullableStringProp("copyright"),
            payload.GetNullableStringProp("userComment"));

        if (mode == WriteMode.DirectInPlace)
            PushUndo($"Edit Camera/Credits ({targets.Length})", targets);

        using var cts = BeginBusy(out var token, $"Updating camera tags ({targets.Length})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var result = await _exifTool.WriteCameraInfoAsync(targets, edit, mode, outDir, progress, token);
            EmitWorkspaceSync();
            return new { written = result.Targets.Count, skipped = result.SkippedFileNames };
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task<object> HandlePickReferenceFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = false,
            Filter = "Media files|*.jpg;*.jpeg;*.heic;*.heif;*.hif;*.png;*.webp;*.avif;*.dng;*.mp4;*.mov;*.m4v|All files|*.*"
        };
        if (dialog.ShowDialog(_window) != true)
            return new { cancelled = true };

        var refItem = new PhotoItem(dialog.FileName);
        await _exifTool.ReadMetadataAsync([refItem]);
        return new
        {
            cancelled = false,
            fileName = refItem.FileName,
            path = refItem.Path,
            hasGps = refItem.Latitude.HasValue && refItem.Longitude.HasValue,
            lat = refItem.Latitude,
            lon = refItem.Longitude,
            alt = refItem.Altitude,
            dateTaken = refItem.DateTaken
        };
    }

    private async Task<object> HandleImportGpxAsync(JsonElement payload)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = false,
            Filter = "GPX Track|*.gpx|All files|*.*"
        };
        if (dialog.ShowDialog(_window) != true)
            return new { cancelled = true };

        var toleranceMin = payload.GetIntProp("toleranceMinutes") ?? 30;
        var offsetHours = payload.GetDoubleProp("offsetHours") ?? 8.0;
        var ids = payload.GetStringArrayProp("ids");
        var targets = ids.Length > 0 ? ResolvePhotosByIds(ids) : SnapshotPhotosRaw();

        var points = await Task.Run(() => GeoTrackService.ParseGpx(dialog.FileName, isFilePath: true));
        var proposals = GeoTrackService.MatchPhotosToTrack(
            targets,
            points,
            TimeSpan.FromMinutes(Math.Clamp(toleranceMin, 1, 720)),
            TimeSpan.FromHours(offsetHours));

        var previewPoly = points.Where((_, idx) => idx % Math.Max(1, points.Count / 250) == 0)
            .Select(pt => new[] { pt.Latitude, pt.Longitude })
            .ToArray();

        return new
        {
            cancelled = false,
            fileName = Path.GetFileName(dialog.FileName),
            pointCount = points.Count,
            polyline = previewPoly,
            proposals
        };
    }

    private async Task<object> HandleApplyGpxMatchesAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("matches", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("No GPX matches provided.");

        var items = new List<(PhotoItem Photo, GpsCoordinate Coord)>();
        foreach (var el in arr.EnumerateArray())
        {
            var id = el.GetStringProp("photoId");
            var lat = el.GetDoubleProp("lat");
            var lon = el.GetDoubleProp("lon");
            var alt = el.GetDoubleProp("alt");
            if (id is null || lat is null || lon is null) continue;
            var photo = FindPhotoById(id);
            if (photo is not null)
                items.Add((photo, new GpsCoordinate(lat.Value, lon.Value, alt)));
        }

        if (items.Count == 0)
            return new { written = 0 };

        var mode = ParseWriteMode(payload.GetStringProp("writeMode"));
        var outDir = payload.GetStringProp("outputDir");
        if (mode == WriteMode.DirectInPlace)
            PushUndo($"GPX Track Match ({items.Count})", items.Select(x => x.Photo).ToArray());

        using var cts = BeginBusy(out var token, $"Writing GPX track GPS ({items.Count})...");
        try
        {
            var progress = new Progress<WriteProgress>(p => EmitProgress(true, p.Message, p.Percent));
            var result = await _exifTool.WriteGpsBatchAsync(items, mode, outDir, progress, token);
            EmitWorkspaceSync();
            return new { written = result.Targets.Count, skipped = result.SkippedFileNames };
        }
        finally
        {
            EndBusy();
        }
    }

    private object HandleExportFootprint(JsonElement payload)
    {
        var format = (payload.GetStringProp("format") ?? "gpx").ToLowerInvariant();
        var ids = payload.GetStringArrayProp("ids");
        var source = ids.Length > 0 ? ResolvePhotosByIds(ids) : SnapshotPhotosRaw();
        var geotagged = source.Where(p => p.Latitude.HasValue && p.Longitude.HasValue).ToArray();
        if (geotagged.Length == 0)
            throw new InvalidOperationException("No geotagged photos available to export.");

        var (filter, ext) = format switch
        {
            "geojson" => ("GeoJSON (*.geojson)|*.geojson", "geojson"),
            "csv" => ("CSV (*.csv)|*.csv", "csv"),
            _ => ("GPX Track (*.gpx)|*.gpx", "gpx")
        };

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = $"photo-footprint-{DateTime.Now:yyyyMMdd}.{ext}"
        };
        if (dlg.ShowDialog(_window) != true)
            return new { cancelled = true };

        var content = format switch
        {
            "geojson" => GeoTrackService.ExportGeoJson(geotagged),
            "csv" => GeoTrackService.ExportCsv(geotagged),
            _ => GeoTrackService.ExportGpx(geotagged)
        };
        File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(false));
        return new { cancelled = false, path = dlg.FileName, count = geotagged.Length };
    }

    private async Task<object> HandleReadAllTagsAsync(JsonElement payload)
    {
        var id = payload.GetStringProp("photoId") ?? "";
        var photo = FindPhotoById(id) ?? throw new FileNotFoundException("Photo not found.");
        var tags = await _exifTool.ReadAllTagsAsync(photo.Path);
        return new { tags };
    }

    private async Task<object> HandleUndoAsync()
    {
        var op = _undoService.Pop();
        if (op is null)
            return new { undone = false };

        using var cts = BeginBusy(out var token, $"Undoing: {op.Description}...");
        try
        {
            await _exifTool.RestoreSnapshotsAsync(op.Snapshots, SnapshotPhotosRaw(), token);
            EmitWorkspaceSync();
            return new { undone = true, description = op.Description };
        }
        finally
        {
            EndBusy();
        }
    }

    private object HandleRevealInExplorer(JsonElement payload)
    {
        var id = payload.GetStringProp("photoId") ?? "";
        var photo = FindPhotoById(id);
        if (photo is not null && File.Exists(photo.Path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{photo.Path}\"",
                UseShellExecute = true
            });
        }
        return new { ok = true };
    }

    private object HandleCancelBusy()
    {
        _busyCts?.Cancel();
        return new { canceled = true };
    }

    private object HandleOpenExternalUrl(JsonElement payload)
    {
        var url = payload.GetStringProp("url") ?? "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return new { ok = true };
    }

    private void PushUndo(string description, IReadOnlyList<PhotoItem> photos)
    {
        var snapshots = photos.Select(p => p.CreateSnapshot()).ToArray();
        _undoService.Push(description, snapshots);
    }

    private void AddRecentLocation(double lat, double lon, double? alt, string? name)
    {
        var roundedLat = Math.Round(lat, 5);
        var roundedLon = Math.Round(lon, 5);
        _settings.RecentLocations.RemoveAll(r =>
            Math.Abs(r.Latitude - roundedLat) < 0.0001 && Math.Abs(r.Longitude - roundedLon) < 0.0001);
        _settings.RecentLocations.Insert(0, new SavedLocationPin
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{roundedLat:0.####}, {roundedLon:0.####}" : name,
            Latitude = Math.Round(lat, 6),
            Longitude = Math.Round(lon, 6),
            Altitude = alt,
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        if (_settings.RecentLocations.Count > 8)
            _settings.RecentLocations.RemoveRange(8, _settings.RecentLocations.Count - 8);
        _settingsService.Save(_settings);
    }

    private static WriteMode ParseWriteMode(string? raw) =>
        string.Equals(raw, "copy", StringComparison.OrdinalIgnoreCase)
            ? WriteMode.CopyToOutputDirectory
            : WriteMode.DirectInPlace;

    private PhotoItem[] SnapshotPhotosRaw()
    {
        lock (_photosLock)
            return [.. _photos];
    }

    private PhotoItem[] ResolvePhotosByIds(string[] ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        lock (_photosLock)
            return _photos.Where(p => set.Contains(p.Id)).ToArray();
    }

    private object[] SnapshotPhotos()
    {
        lock (_photosLock)
        {
            return _photos.Select(p => (object)new
            {
                id = p.Id,
                path = p.Path,
                fileName = p.FileName,
                directory = p.Directory,
                fileSizeBytes = p.FileSizeBytes,
                cameraMake = p.CameraMake,
                cameraModel = p.CameraModel,
                cameraDisplay = p.CameraDisplay,
                lensModel = p.LensModel,
                focalLength = p.FocalLength,
                aperture = p.Aperture,
                shutterSpeed = p.ShutterSpeed,
                iso = p.Iso,
                imageWidth = p.ImageWidth,
                imageHeight = p.ImageHeight,
                colorSpace = p.ColorSpace,
                artist = p.Artist,
                copyright = p.Copyright,
                userComment = p.UserComment,
                dateTaken = p.DateTaken,
                dateTakenDate = p.DateTakenDate,
                dateTakenTime = p.DateTakenTime,
                offsetTime = p.OffsetTime,
                fileCreationTime = p.FileCreationTime == default ? null : p.FileCreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                latitude = p.Latitude,
                longitude = p.Longitude,
                altitude = p.Altitude,
                gpsDisplay = p.GpsDisplay,
                hasGps = p.Latitude.HasValue && p.Longitude.HasValue,
                format = p.FormatDisplay,
                isVideo = p.IsVideo,
                isMetadataWritable = p.IsMetadataWritable,
                isExtensionMismatched = p.IsExtensionMismatched,
                wrongExtensionHint = p.WrongExtensionHint,
                status = p.Status,
                solar = p.SolarInfo
            }).ToArray();
        }
    }

    private object BuildUndoDto() => new
    {
        canUndo = _undoService.CanUndo,
        count = _undoService.Count,
        description = _undoService.PeekDescription
    };

    private CancellationTokenSource BeginBusy(out CancellationToken token, string message)
    {
        if (_isBusy)
            throw new InvalidOperationException("Another metadata task is currently running.");
        _isBusy = true;
        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        token = _busyCts.Token;
        EmitProgress(true, message, null);
        return _busyCts;
    }

    private void EndBusy()
    {
        _isBusy = false;
        EmitProgress(false, "", null);
    }

    public void EmitWindowState()
    {
        PostEvent("windowState", new { maximized = _window.WindowState == WindowState.Maximized });
    }

    private void EmitWorkspaceSync()
    {
        PostEvent("photosUpdated", new
        {
            photos = SnapshotPhotos(),
            undo = BuildUndoDto(),
            recentLocations = _settings.RecentLocations
        });
    }

    private void EmitProgress(bool busy, string message, double? percent) =>
        PostEvent("progress", new { busy, message, percent });

    private void EmitToast(string kind, string message) =>
        PostEvent("toast", new { kind, message });

    private void PostResponse(string reqId, bool ok, object? data, string? error)
    {
        var json = JsonSerializer.Serialize(new { kind = "response", reqId, ok, data, error }, JsonOptions);
        _window.Dispatcher.InvokeAsync(() => _webView.PostWebMessageAsJson(json));
    }

    private void PostEvent(string eventName, object data)
    {
        var json = JsonSerializer.Serialize(new { kind = "event", eventName, data }, JsonOptions);
        _window.Dispatcher.InvokeAsync(() => _webView.PostWebMessageAsJson(json));
    }

    public ValueTask DisposeAsync()
    {
        _busyCts?.Cancel();
        _busyCts?.Dispose();
        return _exifTool.DisposeAsync();
    }
}

internal static class JsonElementExtensions
{
    public static string? GetStringProp(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public static string? GetNullableStringProp(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    public static bool GetBoolProp(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    public static int? GetIntProp(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v)
            ? v
            : null;

    public static long? GetLongProp(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.TryGetInt64(out var v)
            ? v
            : null;

    public static double? GetDoubleProp(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v)
            ? v
            : null;

    public static string[] GetStringArrayProp(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return [];
        return p.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToArray();
    }
}
