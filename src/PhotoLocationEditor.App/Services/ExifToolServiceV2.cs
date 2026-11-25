using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public sealed class ExifToolService : IAsyncDisposable
{
    private const int ReadChunkSize = 80;
    private readonly string _exifToolPath;
    private readonly ExifToolDaemon _daemon;

    public ExifToolService(string exifToolPath)
    {
        _exifToolPath = exifToolPath;
        _daemon = new ExifToolDaemon(exifToolPath);
    }

    public static string DefaultPath => Path.Combine(
        AppContext.BaseDirectory,
        "Tools",
        "exiftool.exe");

    public bool IsDaemonRunning => _daemon.IsRunning;

    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        _daemon.WarmUpAsync(cancellationToken);

    public static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".hif" or ".png" or ".webp" or ".avif"
            or ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".raf"
            or ".mp4" or ".mov" or ".avi" or ".mkv" or ".3gp" or ".m4v" or ".wmv" or ".mts" or ".m2ts";
    }

    public static bool IsSupportedMetadataWrite(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return IsImageMetadataWrite(extension) || IsQuickTimeVideoMetadataWrite(extension);
    }

    private static bool IsImageMetadataWrite(string extension) =>
        extension is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".hif" or ".png" or ".webp" or ".avif" or ".dng";

    private static bool IsQuickTimeVideoMetadataWrite(string extension) =>
        extension is ".mp4" or ".mov" or ".m4v" or ".3gp";

    public async Task ReadMetadataAsync(
        IReadOnlyList<PhotoItem> photos,
        IProgress<WriteProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0)
            return;

        string[] readTags =
        [
            "-j", "-a", "-G1", "-s", "-n",
            "-IFD0:Make", "-ExifIFD:Make", "-Composite:Make",
            "-IFD0:Model", "-ExifIFD:Model", "-Composite:Model",
            "-ExifIFD:LensModel", "-XMP-aux:Lens", "-Composite:LensID",
            "-ExifIFD:FocalLength", "-ExifIFD:FNumber", "-ExifIFD:ExposureTime", "-ExifIFD:ISO",
            "-File:ImageWidth", "-File:ImageHeight", "-ExifIFD:ExifImageWidth", "-ExifIFD:ExifImageHeight",
            "-IFD0:Orientation", "-ExifIFD:ColorSpace",
            "-IFD0:Artist", "-IFD0:Copyright", "-ExifIFD:UserComment",
            "-ExifIFD:DateTimeOriginal", "-ExifIFD:CreateDate",
            "-QuickTime:CreateDate", "-XMP-exif:DateTimeOriginal",
            "-IFD0:ModifyDate", "-ExifIFD:OffsetTimeOriginal",
            "-GPS:GPSLatitude", "-GPS:GPSLongitude", "-GPS:GPSAltitude",
            "-Composite:GPSLatitude", "-Composite:GPSLongitude", "-Composite:GPSAltitude"
        ];

        var processed = 0;
        for (var offset = 0; offset < photos.Count; offset += ReadChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = photos.Skip(offset).Take(ReadChunkSize).ToArray();
            var args = new List<string>(readTags.Length + chunk.Length);
            args.AddRange(readTags);
            foreach (var p in chunk)
                args.Add(p.Path);

            var result = await RunWithDaemonOrFallbackAsync(args, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException(result.Error);

            var output = result.Output;
            if (!string.IsNullOrWhiteSpace(output))
            {
                await Task.Run(() =>
                {
                    using var document = JsonDocument.Parse(output);
                    var items = document.RootElement.EnumerateArray().ToArray();
                    for (var i = 0; i < chunk.Length && i < items.Length; i++)
                        ApplyMetadata(chunk[i], items[i]);
                }, cancellationToken).ConfigureAwait(false);
            }

            processed += chunk.Length;
            progress?.Report(new WriteProgress(
                $"Metadata {processed}/{photos.Count}",
                processed * 100.0 / photos.Count));
        }
    }

    public Task ReadMetadataAsync(IReadOnlyList<PhotoItem> photos, CancellationToken cancellationToken = default) =>
        ReadMetadataAsync(photos, null, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> ReadAllTagsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return new Dictionary<string, string>();

        var result = await RunWithDaemonOrFallbackAsync(["-j", "-G1", "-s", filePath], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Output))
            return new Dictionary<string, string>();

        using var doc = JsonDocument.Parse(result.Output);
        var arr = doc.RootElement;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return new Dictionary<string, string>();

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in arr[0].EnumerateObject())
        {
            if (prop.Name.Equals("SourceFile", StringComparison.OrdinalIgnoreCase))
                continue;
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.ToString();
        }
        return dict;
    }

    public async Task<byte[]?> ExtractEmbeddedPreviewAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_exifToolPath) || !File.Exists(filePath))
            return null;

        foreach (var tag in new[] { "-PreviewImage", "-JpgFromRaw", "-ThumbnailImage" })
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _exifToolPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-charset");
            startInfo.ArgumentList.Add("filename=utf8");
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add(tag);
            startInfo.ArgumentList.Add(filePath);

            using var proc = Process.Start(startInfo);
            if (proc is null) continue;

            using var ms = new MemoryStream();
            var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
            var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(copyTask, errTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

            if (ms.Length > 256)
                return ms.ToArray();
        }

        return null;
    }

    public async Task<WriteResult> WriteGpsAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        GpsCoordinate coordinate,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = FilterWritable(selectedPhotos);
        if (plan.Writable.Count == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(plan.Skipped));

        var targets = mode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(plan.Writable, mode, outputDirectory, progress), cancellationToken).ConfigureAwait(false)
            : PrepareTargets(plan.Writable, mode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), plan.Skipped);

        progress?.Report(new WriteProgress("Writing GPS metadata with ExifTool...", null));

        var imageTargets = targets.Where(t => IsImageMetadataWrite(Path.GetExtension(t.Path).ToLowerInvariant())).ToArray();
        var quickTimeTargets = targets.Where(t => IsQuickTimeVideoMetadataWrite(Path.GetExtension(t.Path).ToLowerInvariant())).ToArray();

        if (imageTargets.Length > 0)
            await WriteImageGpsAsync(imageTargets, coordinate, cancellationToken).ConfigureAwait(false);

        if (quickTimeTargets.Length > 0)
            await WriteQuickTimeGpsAsync(quickTimeTargets, coordinate, cancellationToken).ConfigureAwait(false);

        var fujifilmGroups = imageTargets
            .Where(target => IsFujifilm(target.Photo) && NeedsMakeModelFix(target.Photo))
            .GroupBy(target => BuildFujifilmDisplayModel(target.Photo.CameraModel))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToArray();

        if (fujifilmGroups.Length > 0)
            await WriteFujifilmDisplayModelAsync(fujifilmGroups, progress, cancellationToken).ConfigureAwait(false);

        foreach (var target in targets.Where(t => string.Equals(t.Photo.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
        {
            target.Photo.Latitude = coordinate.Latitude;
            target.Photo.Longitude = coordinate.Longitude;
            if (coordinate.Altitude.HasValue)
                target.Photo.Altitude = coordinate.Altitude.Value;
        }

        return new WriteResult(targets.Select(target => target.Path).ToArray(), plan.Skipped);
    }

    public async Task<WriteResult> WriteGpsBatchAsync(
        IReadOnlyList<(PhotoItem Photo, GpsCoordinate Coordinate)> items,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return new WriteResult(Array.Empty<string>(), Array.Empty<string>());

        var writable = items.Where(i => IsSupportedMetadataWrite(i.Photo.Path)).ToArray();
        var skipped = items.Where(i => !IsSupportedMetadataWrite(i.Photo.Path)).Select(i => i.Photo.FileName).ToArray();
        if (writable.Length == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(skipped));

        var photos = writable.Select(i => i.Photo).ToArray();
        var targets = mode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(photos, mode, outputDirectory, progress), cancellationToken).ConfigureAwait(false)
            : PrepareTargets(photos, mode, outputDirectory, progress);

        var args = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) args.Add("-execute");
            var target = targets[i];
            var coord = writable[i].Coordinate;
            args.Add("-overwrite_original");
            args.Add("-P");
            if (IsQuickTimeVideoMetadataWrite(Path.GetExtension(target.Path).ToLowerInvariant()))
            {
                args.Add($"-QuickTime:GPSCoordinates={FormatQuickTimeGps(coord)}");
            }
            else
            {
                args.Add("-n");
                args.Add($"-GPSLatitude={Math.Abs(coord.Latitude).ToString("R", CultureInfo.InvariantCulture)}");
                args.Add($"-GPSLatitudeRef={coord.LatitudeRef}");
                args.Add($"-GPSLongitude={Math.Abs(coord.Longitude).ToString("R", CultureInfo.InvariantCulture)}");
                args.Add($"-GPSLongitudeRef={coord.LongitudeRef}");
                args.Add("-GPSVersionID=2 2 0 0");
                if (coord.Altitude.HasValue)
                {
                    args.Add($"-GPSAltitude={Math.Abs(coord.Altitude.Value).ToString("R", CultureInfo.InvariantCulture)}");
                    args.Add(coord.Altitude.Value < 0 ? "-GPSAltitudeRef=1" : "-GPSAltitudeRef=0");
                }
            }
            args.Add(target.Path);
        }

        progress?.Report(new WriteProgress($"Writing matched GPS ({targets.Count})...", null));
        var result = await RunWithDaemonOrFallbackAsync(args, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (string.Equals(t.Photo.Path, t.Path, StringComparison.OrdinalIgnoreCase))
            {
                t.Photo.Latitude = writable[i].Coordinate.Latitude;
                t.Photo.Longitude = writable[i].Coordinate.Longitude;
                if (writable[i].Coordinate.Altitude is double alt)
                    t.Photo.Altitude = alt;
            }
        }

        return new WriteResult(targets.Select(t => t.Path).ToArray(), skipped);
    }

    public async Task<WriteResult> ClearMetadataAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        PrivacyStripMode stripMode,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = FilterWritable(selectedPhotos);
        if (plan.Writable.Count == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(plan.Skipped));

        var targets = mode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(plan.Writable, mode, outputDirectory, progress), cancellationToken).ConfigureAwait(false)
            : PrepareTargets(plan.Writable, mode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), plan.Skipped);

        progress?.Report(new WriteProgress("Cleaning metadata...", null));

        var args = new List<string> { "-overwrite_original", "-P" };
        if (stripMode == PrivacyStripMode.GpsOnly)
        {
            args.Add("-GPS:all=");
            args.Add("-XMP:GPSLatitude=");
            args.Add("-XMP:GPSLongitude=");
            args.Add("-XMP:GPSAltitude=");
            args.Add("-QuickTime:GPSCoordinates=");
        }
        else if (stripMode == PrivacyStripMode.PrivacyCleanKeepExif)
        {
            args.Add("-GPS:all=");
            args.Add("-XMP:GPSLatitude=");
            args.Add("-XMP:GPSLongitude=");
            args.Add("-QuickTime:GPSCoordinates=");
            args.Add("-SerialNumber=");
            args.Add("-InternalSerialNumber=");
            args.Add("-LensSerialNumber=");
            args.Add("-CameraSerialNumber=");
            args.Add("-MakerNotes:all=");
            args.Add("-IFD0:Software=");
            args.Add("-XMP:History=");
            args.Add("-IFD0:Artist=");
            args.Add("-IFD0:Copyright=");
            args.Add("-ExifIFD:UserComment=");
        }
        else
        {
            // Strip everything except ICC profile & Orientation so colors and rotation stay intact
            args.Add("-all=");
            args.Add("-tagsFromFile");
            args.Add("@");
            args.Add("-icc_profile");
            args.Add("-Orientation");
        }

        await RunForTargetsAsync(args, targets, cancellationToken).ConfigureAwait(false);

        foreach (var t in targets.Where(t => string.Equals(t.Photo.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
        {
            t.Photo.Latitude = null;
            t.Photo.Longitude = null;
            t.Photo.Altitude = null;
            if (stripMode == PrivacyStripMode.PrivacyCleanFull)
            {
                t.Photo.DateTaken = null;
                t.Photo.CameraMake = null;
                t.Photo.CameraModel = null;
                t.Photo.LensModel = null;
                t.Photo.Artist = null;
                t.Photo.Copyright = null;
                t.Photo.UserComment = null;
            }
            else if (stripMode == PrivacyStripMode.PrivacyCleanKeepExif)
            {
                t.Photo.Artist = null;
                t.Photo.Copyright = null;
                t.Photo.UserComment = null;
            }
        }

        return new WriteResult(targets.Select(t => t.Path).ToArray(), plan.Skipped);
    }

    public async Task<WriteResult> WriteCameraInfoAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        CameraInfoEdit edit,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = FilterWritable(selectedPhotos);
        if (plan.Writable.Count == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(plan.Skipped));

        var targets = mode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(plan.Writable, mode, outputDirectory, progress), cancellationToken).ConfigureAwait(false)
            : PrepareTargets(plan.Writable, mode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), plan.Skipped);

        var args = new List<string> { "-overwrite_original", "-P" };
        if (edit.Make is not null) args.Add($"-Make={edit.Make}");
        if (edit.Model is not null) args.Add($"-Model={edit.Model}");
        if (edit.LensModel is not null) args.Add($"-LensModel={edit.LensModel}");
        if (edit.Artist is not null) args.Add($"-Artist={edit.Artist}");
        if (edit.Copyright is not null) args.Add($"-Copyright={edit.Copyright}");
        if (edit.UserComment is not null) args.Add($"-UserComment={edit.UserComment}");

        if (args.Count == 2)
            return new WriteResult(Array.Empty<string>(), plan.Skipped);

        progress?.Report(new WriteProgress("Writing camera & attribution tags...", null));
        await RunForTargetsAsync(args, targets, cancellationToken).ConfigureAwait(false);

        foreach (var t in targets.Where(t => string.Equals(t.Photo.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
        {
            if (edit.Make is not null) t.Photo.CameraMake = string.IsNullOrEmpty(edit.Make) ? null : edit.Make;
            if (edit.Model is not null) t.Photo.CameraModel = string.IsNullOrEmpty(edit.Model) ? null : edit.Model;
            if (edit.LensModel is not null) t.Photo.LensModel = string.IsNullOrEmpty(edit.LensModel) ? null : edit.LensModel;
            if (edit.Artist is not null) t.Photo.Artist = string.IsNullOrEmpty(edit.Artist) ? null : edit.Artist;
            if (edit.Copyright is not null) t.Photo.Copyright = string.IsNullOrEmpty(edit.Copyright) ? null : edit.Copyright;
            if (edit.UserComment is not null) t.Photo.UserComment = string.IsNullOrEmpty(edit.UserComment) ? null : edit.UserComment;
        }

        return new WriteResult(targets.Select(t => t.Path).ToArray(), plan.Skipped);
    }

    public async Task RestoreSnapshotsAsync(
        IReadOnlyList<MetadataSnapshot> snapshots,
        IReadOnlyList<PhotoItem> livePhotos,
        CancellationToken cancellationToken = default)
    {
        if (snapshots.Count == 0)
            return;

        var args = new List<string>();
        var validCount = 0;

        foreach (var snap in snapshots)
        {
            var currentPath = snap.CurrentPath;
            if (!File.Exists(currentPath))
                continue;

            // Revert extension rename if needed
            if (!string.Equals(snap.CurrentPath, snap.OriginalPath, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(snap.OriginalPath))
            {
                File.Move(snap.CurrentPath, snap.OriginalPath);
                var live = livePhotos.FirstOrDefault(p => string.Equals(p.Path, snap.CurrentPath, StringComparison.OrdinalIgnoreCase));
                if (live is not null)
                {
                    live.UpdatePath(snap.OriginalPath);
                    live.SetDetectedFormat(FormatDetector.Detect(snap.OriginalPath));
                }
                currentPath = snap.OriginalPath;
            }

            if (!IsSupportedMetadataWrite(currentPath))
                continue;

            if (validCount > 0)
                args.Add("-execute");
            validCount++;

            args.Add("-overwrite_original");
            args.Add("-P");

            var isQt = IsQuickTimeVideoMetadataWrite(Path.GetExtension(currentPath).ToLowerInvariant());
            if (snap.Latitude.HasValue && snap.Longitude.HasValue)
            {
                var coord = new GpsCoordinate(snap.Latitude.Value, snap.Longitude.Value, snap.Altitude);
                if (isQt)
                {
                    args.Add($"-QuickTime:GPSCoordinates={FormatQuickTimeGps(coord)}");
                }
                else
                {
                    args.Add("-n");
                    args.Add($"-GPSLatitude={Math.Abs(coord.Latitude).ToString("R", CultureInfo.InvariantCulture)}");
                    args.Add($"-GPSLatitudeRef={coord.LatitudeRef}");
                    args.Add($"-GPSLongitude={Math.Abs(coord.Longitude).ToString("R", CultureInfo.InvariantCulture)}");
                    args.Add($"-GPSLongitudeRef={coord.LongitudeRef}");
                    if (coord.Altitude.HasValue)
                    {
                        args.Add($"-GPSAltitude={Math.Abs(coord.Altitude.Value).ToString("R", CultureInfo.InvariantCulture)}");
                        args.Add(coord.Altitude.Value < 0 ? "-GPSAltitudeRef=1" : "-GPSAltitudeRef=0");
                    }
                    else
                    {
                        args.Add("-GPSAltitude=");
                        args.Add("-GPSAltitudeRef=");
                    }
                }
            }
            else
            {
                args.Add("-GPS:all=");
                args.Add("-XMP:GPSLatitude=");
                args.Add("-XMP:GPSLongitude=");
                args.Add("-QuickTime:GPSCoordinates=");
            }

            var dt = snap.DateTaken ?? string.Empty;
            if (isQt)
                AddQuickTimeDateArguments(args, dt);
            else
            {
                args.Add($"-DateTimeOriginal={dt}");
                args.Add($"-CreateDate={dt}");
                args.Add($"-ModifyDate={dt}");
            }

            if (!isQt)
            {
                args.Add($"-Make={snap.CameraMake ?? ""}");
                args.Add($"-Model={snap.CameraModel ?? ""}");
                args.Add($"-LensModel={snap.LensModel ?? ""}");
                args.Add($"-Artist={snap.Artist ?? ""}");
                args.Add($"-Copyright={snap.Copyright ?? ""}");
            }

            args.Add(currentPath);
        }

        if (validCount > 0)
        {
            var result = await RunWithDaemonOrFallbackAsync(args, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }

        foreach (var snap in snapshots)
        {
            var photo = livePhotos.FirstOrDefault(p =>
                string.Equals(p.Path, snap.OriginalPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Path, snap.CurrentPath, StringComparison.OrdinalIgnoreCase));
            if (photo is not null)
            {
                photo.Latitude = snap.Latitude;
                photo.Longitude = snap.Longitude;
                photo.Altitude = snap.Altitude;
                photo.DateTaken = snap.DateTaken;
                photo.CameraMake = snap.CameraMake;
                photo.CameraModel = snap.CameraModel;
                photo.LensModel = snap.LensModel;
                photo.Artist = snap.Artist;
                photo.Copyright = snap.Copyright;
                photo.Status = "Reverted";
            }
        }
    }

    private async Task WriteImageGpsAsync(
        IReadOnlyList<WriteTarget> targets,
        GpsCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-overwrite_original", "-P", "-n",
            $"-GPSLatitude={Math.Abs(coordinate.Latitude).ToString("R", CultureInfo.InvariantCulture)}",
            $"-GPSLatitudeRef={coordinate.LatitudeRef}",
            $"-GPSLongitude={Math.Abs(coordinate.Longitude).ToString("R", CultureInfo.InvariantCulture)}",
            $"-GPSLongitudeRef={coordinate.LongitudeRef}",
            "-GPSVersionID=2 2 0 0"
        };

        if (coordinate.Altitude.HasValue)
        {
            arguments.Add($"-GPSAltitude={Math.Abs(coordinate.Altitude.Value).ToString("R", CultureInfo.InvariantCulture)}");
            arguments.Add(coordinate.Altitude.Value < 0 ? "-GPSAltitudeRef=1" : "-GPSAltitudeRef=0");
        }

        await RunForTargetsAsync(arguments, targets, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteQuickTimeGpsAsync(
        IReadOnlyList<WriteTarget> targets,
        GpsCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-overwrite_original", "-P",
            $"-QuickTime:GPSCoordinates={FormatQuickTimeGps(coordinate)}"
        };

        await RunForTargetsAsync(arguments, targets, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatQuickTimeGps(GpsCoordinate coordinate)
    {
        var latitude = coordinate.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
        var longitude = coordinate.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
        return coordinate.Altitude.HasValue
            ? string.Join(", ", latitude, longitude, coordinate.Altitude.Value.ToString("0.#", CultureInfo.InvariantCulture))
            : string.Join(", ", latitude, longitude);
    }

    private async Task RunForTargetsAsync(
        IReadOnlyList<string> baseArguments,
        IReadOnlyList<WriteTarget> targets,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(baseArguments.Count + targets.Count);
        arguments.AddRange(baseArguments);
        foreach (var t in targets)
            arguments.Add(t.Path);

        var result = await RunWithDaemonOrFallbackAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private static void ApplyMetadata(PhotoItem photo, JsonElement item)
    {
        photo.CameraMake = TryGetString(item, "IFD0:Make") ??
                           TryGetString(item, "ExifIFD:Make") ??
                           TryGetString(item, "Composite:Make");
        photo.CameraModel = TryGetString(item, "IFD0:Model") ??
                            TryGetString(item, "ExifIFD:Model") ??
                            TryGetString(item, "Composite:Model");
        photo.LensModel = TryGetString(item, "ExifIFD:LensModel") ??
                          TryGetString(item, "XMP-aux:Lens") ??
                          TryGetString(item, "Composite:LensID");
        photo.FocalLength = FormatFocal(TryGetString(item, "ExifIFD:FocalLength"));
        photo.Aperture = FormatAperture(TryGetDouble(item, "ExifIFD:FNumber"));
        photo.ShutterSpeed = FormatExposure(TryGetDouble(item, "ExifIFD:ExposureTime"));
        photo.Iso = TryGetInt(item, "ExifIFD:ISO");
        photo.ImageWidth = TryGetInt(item, "File:ImageWidth") ?? TryGetInt(item, "ExifIFD:ExifImageWidth");
        photo.ImageHeight = TryGetInt(item, "File:ImageHeight") ?? TryGetInt(item, "ExifIFD:ExifImageHeight");
        photo.Orientation = TryGetString(item, "IFD0:Orientation");
        photo.ColorSpace = TryGetString(item, "ExifIFD:ColorSpace");
        photo.Artist = TryGetString(item, "IFD0:Artist");
        photo.Copyright = TryGetString(item, "IFD0:Copyright");
        photo.UserComment = TryGetString(item, "ExifIFD:UserComment");
        photo.OffsetTime = TryGetString(item, "ExifIFD:OffsetTimeOriginal");

        photo.DateTaken = TryGetString(item, "ExifIFD:DateTimeOriginal") ??
                          TryGetString(item, "ExifIFD:CreateDate") ??
                          TryGetString(item, "QuickTime:CreateDate") ??
                          TryGetString(item, "XMP-exif:DateTimeOriginal") ??
                          TryGetString(item, "IFD0:ModifyDate");
        photo.Latitude = TryGetDouble(item, "GPS:GPSLatitude") ?? TryGetDouble(item, "Composite:GPSLatitude");
        photo.Longitude = TryGetDouble(item, "GPS:GPSLongitude") ?? TryGetDouble(item, "Composite:GPSLongitude");
        photo.Altitude = TryGetDouble(item, "GPS:GPSAltitude") ?? TryGetDouble(item, "Composite:GPSAltitude");
        photo.Status = "Loaded";
        photo.RefreshComputed();
    }

    private static string? FormatFocal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return $"{f:0.#}mm";
        return raw.Contains("mm", StringComparison.OrdinalIgnoreCase) ? raw : $"{raw}mm";
    }

    private static string? FormatAperture(double? fNumber) =>
        fNumber is > 0 ? $"f/{fNumber.Value:0.#}" : null;

    private static string? FormatExposure(double? seconds)
    {
        if (seconds is null or <= 0) return null;
        if (seconds.Value >= 1.0)
            return $"{seconds.Value:0.#}s";
        var denom = Math.Round(1.0 / seconds.Value);
        return denom >= 2 ? $"1/{denom:0}s" : $"{seconds.Value:0.##}s";
    }

    private static string? TryGetString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;
    }

    private static double? TryGetDouble(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
            return value;
        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? TryGetInt(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return int.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private async Task WriteFujifilmDisplayModelAsync(
        IReadOnlyList<IGrouping<string, WriteTarget>> targetGroups,
        IProgress<WriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < targetGroups.Count; index++)
        {
            var group = targetGroups[index];
            progress?.Report(new WriteProgress($"Writing FUJIFILM model metadata ({index + 1}/{targetGroups.Count})...", null));
            var arguments = new List<string>
            {
                "-overwrite_original", "-P",
                "-Make=FUJIFILM",
                $"-Model={group.Key}"
            };
            await RunForTargetsAsync(arguments, group.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsFujifilm(PhotoItem photo)
    {
        return ContainsFujifilm(photo.CameraMake) ||
               ContainsFujifilm(photo.CameraModel) ||
               photo.FileName.StartsWith("DSCF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsFujifilm(string? value) =>
        value?.Contains("FUJIFILM", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("FUJI", StringComparison.OrdinalIgnoreCase) == true;

    private static bool NeedsMakeModelFix(PhotoItem photo)
    {
        var make = photo.CameraMake ?? "";
        var model = photo.CameraModel ?? "";
        if (string.IsNullOrWhiteSpace(model))
            return false;
        return !ContainsFujifilm(make) || !model.Contains("FUJIFILM", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFujifilmDisplayModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "";
        var normalized = model.Trim();
        return normalized.StartsWith("FUJIFILM", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"FUJIFILM {normalized}";
    }

    private static List<WriteTarget> PrepareTargets(
        IReadOnlyList<PhotoItem> selectedPhotos,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress)
    {
        return mode == WriteMode.CopyToOutputDirectory
            ? CopyToOutputDirectory(selectedPhotos, outputDirectory, progress)
            : DirectInPlace(selectedPhotos, progress);
    }

    private static List<WriteTarget> CopyToOutputDirectory(
        IReadOnlyList<PhotoItem> selectedPhotos,
        string? outputDirectory,
        IProgress<WriteProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("Choose an output directory first.");

        Directory.CreateDirectory(outputDirectory);
        var targets = new List<WriteTarget>();

        for (var index = 0; index < selectedPhotos.Count; index++)
        {
            var photo = selectedPhotos[index];
            var destination = GetUniquePath(Path.Combine(outputDirectory, photo.FileName));
            File.Copy(photo.Path, destination);
            try
            {
                File.SetCreationTime(destination, File.GetCreationTime(photo.Path));
                File.SetLastWriteTime(destination, File.GetLastWriteTime(photo.Path));
            }
            catch { }

            targets.Add(new WriteTarget(photo, destination));
            photo.Status = $"Copied: {Path.GetFileName(destination)}";
            progress?.Report(new WriteProgress(
                $"Copying photos ({index + 1}/{selectedPhotos.Count})...",
                (index + 1) * 100d / selectedPhotos.Count));
        }

        return targets;
    }

    private static List<WriteTarget> DirectInPlace(
        IReadOnlyList<PhotoItem> selectedPhotos,
        IProgress<WriteProgress>? progress)
    {
        var targets = new List<WriteTarget>();
        for (var index = 0; index < selectedPhotos.Count; index++)
        {
            var photo = selectedPhotos[index];
            photo.Status = "Direct write";
            targets.Add(new WriteTarget(photo, photo.Path));
            progress?.Report(new WriteProgress(
                $"Preparing direct write ({index + 1}/{selectedPhotos.Count})...",
                (index + 1) * 100d / selectedPhotos.Count));
        }
        return targets;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    public async Task<WriteResult> WriteDateAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        string dateTimeOriginal,
        WriteMode mode,
        string? outputDirectory,
        bool syncFileTimestamps = false,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var batch = selectedPhotos.Select(p => (p, dateTimeOriginal)).ToArray();
        return await WriteDateBatchAsync(batch, progress, mode, outputDirectory, syncFileTimestamps, cancellationToken).ConfigureAwait(false);
    }

    public Task<WriteResult> WriteDateBatchAsync(
        IReadOnlyList<(PhotoItem Photo, string Date)> items,
        IProgress<WriteProgress>? progress,
        WriteMode writeMode,
        string? outputDirectory,
        CancellationToken cancellationToken = default) =>
        WriteDateBatchAsync(items, progress, writeMode, outputDirectory, syncFileTimestamps: false, cancellationToken);

    public async Task<WriteResult> WriteDateBatchAsync(
        IReadOnlyList<(PhotoItem Photo, string Date)> items,
        IProgress<WriteProgress>? progress,
        WriteMode writeMode,
        string? outputDirectory,
        bool syncFileTimestamps,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return new WriteResult(Array.Empty<string>(), Array.Empty<string>());

        var writableItems = items.Where(i => IsSupportedMetadataWrite(i.Photo.Path)).ToArray();
        var skipped = items.Where(i => !IsSupportedMetadataWrite(i.Photo.Path)).Select(i => i.Photo.FileName).ToArray();

        if (writableItems.Length == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(skipped));

        var photos = writableItems.Select(i => i.Photo).ToArray();
        var targets = writeMode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(photos, writeMode, outputDirectory, progress), cancellationToken).ConfigureAwait(false)
            : PrepareTargets(photos, writeMode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), skipped);

        var lines = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0) lines.Add("-execute");
            var dt = writableItems[i].Date;
            lines.Add("-overwrite_original");
            lines.Add("-P");
            if (IsQuickTimeVideoMetadataWrite(Path.GetExtension(targets[i].Path).ToLowerInvariant()))
                AddQuickTimeDateArguments(lines, dt);
            else
            {
                lines.Add($"-DateTimeOriginal={dt}");
                lines.Add($"-CreateDate={dt}");
                lines.Add($"-ModifyDate={dt}");
            }
            lines.Add(targets[i].Path);
        }

        progress?.Report(new WriteProgress("Writing dates with ExifTool...", null));
        var result = await RunWithDaemonOrFallbackAsync(lines, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var dtStr = writableItems[i].Date;

            if (syncFileTimestamps && DateIntelligenceService.TryParseExifDateTime(dtStr, out var parsedDt))
            {
                try
                {
                    File.SetCreationTime(target.Path, parsedDt);
                    File.SetLastWriteTime(target.Path, parsedDt);
                }
                catch { }
            }

            if (string.Equals(target.Photo.Path, target.Path, StringComparison.OrdinalIgnoreCase))
            {
                target.Photo.DateTaken = dtStr;
                if (syncFileTimestamps && DateIntelligenceService.TryParseExifDateTime(dtStr, out var parsedDt2))
                {
                    target.Photo.SetFileCreationTime(parsedDt2);
                    target.Photo.FileModificationTime = parsedDt2;
                }
            }

            progress?.Report(new WriteProgress(
                $"Date written ({i + 1}/{targets.Count})...",
                (i + 1) * 100d / targets.Count));
        }

        return new WriteResult(targets.Select(t => t.Path).ToArray(), skipped);
    }

    private static void AddQuickTimeDateArguments(List<string> arguments, string dateTimeOriginal)
    {
        arguments.Add($"-QuickTime:CreateDate={dateTimeOriginal}");
        arguments.Add($"-QuickTime:ModifyDate={dateTimeOriginal}");
        arguments.Add($"-TrackCreateDate={dateTimeOriginal}");
        arguments.Add($"-TrackModifyDate={dateTimeOriginal}");
        arguments.Add($"-MediaCreateDate={dateTimeOriginal}");
        arguments.Add($"-MediaModifyDate={dateTimeOriginal}");
    }

    private static (IReadOnlyList<PhotoItem> Writable, string[] Skipped) FilterWritable(IReadOnlyList<PhotoItem> photos)
    {
        var writable = photos.Where(p => IsSupportedMetadataWrite(p.Path)).ToArray();
        var skipped = photos.Where(p => !IsSupportedMetadataWrite(p.Path)).Select(p => p.FileName).ToArray();
        return (writable, skipped);
    }

    private static string BuildUnsupportedMessage(IReadOnlyList<string> skipped)
    {
        var examples = string.Join(", ", skipped.Take(5));
        var more = skipped.Count > 5 ? $" (+{skipped.Count - 5})" : "";
        return "Metadata writing is enabled for JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP/AVIF/DNG and QuickTime videos (MP4/MOV/M4V/3GP). " +
               $"Skipped {skipped.Count} read-only file(s): {examples}{more}";
    }

    private async Task<(int ExitCode, string Output, string Error)> RunWithDaemonOrFallbackAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _daemon.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fallback to one-shot -@ argfile if persistent daemon failed unexpectedly
            return await RunOneShotAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunOneShotAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_exifToolPath))
            throw new FileNotFoundException("ExifTool was not found.", _exifToolPath);

        var argFile = Path.GetTempFileName();
        await File.WriteAllLinesAsync(argFile, arguments, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _exifToolPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-charset");
            startInfo.ArgumentList.Add("filename=utf8");
            startInfo.ArgumentList.Add("-charset");
            startInfo.ArgumentList.Add("exif=utf8");
            startInfo.ArgumentList.Add("-@");
            startInfo.ArgumentList.Add(argFile);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start ExifTool.");

            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
        finally
        {
            try { File.Delete(argFile); } catch { }
        }
    }

    public async Task CopyMetadataFromAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath) || !File.Exists(destinationPath))
            return;

        var arguments = new List<string>
        {
            "-overwrite_original",
            "-TagsFromFile", sourcePath,
            "-all:all",
            destinationPath
        };

        var result = await RunWithDaemonOrFallbackAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    public ValueTask DisposeAsync() => _daemon.DisposeAsync();

    private sealed record WriteTarget(PhotoItem Photo, string Path);
}

public sealed record WriteResult(
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> SkippedFileNames);

public sealed record WriteProgress(string Message, double? Percent);
