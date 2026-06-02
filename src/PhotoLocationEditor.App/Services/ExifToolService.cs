using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public sealed class ExifToolService
{
    private readonly string _exifToolPath;

    public ExifToolService(string exifToolPath)
    {
        _exifToolPath = exifToolPath;
    }

    public static string DefaultPath => Path.Combine(
        AppContext.BaseDirectory,
        "Tools",
        "exiftool.exe");

    public static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".hif" or ".png" or ".webp"
            or ".mp4" or ".mov" or ".avi" or ".mkv" or ".3gp" or ".m4v" or ".wmv" or ".mts" or ".m2ts";
    }

    public static bool IsSupportedMetadataWrite(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return IsImageMetadataWrite(path) || IsQuickTimeVideoMetadataWrite(path);
    }

    private static bool IsImageMetadataWrite(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".hif" or ".png" or ".webp";
    }

    private static bool IsQuickTimeVideoMetadataWrite(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".mp4" or ".mov" or ".m4v" or ".3gp";
    }

    public async Task ReadMetadataAsync(IReadOnlyList<PhotoItem> photos, CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return;

        var arguments = new List<string>
        {
            "-j",
            "-a",
            "-G1",
            "-s",
            "-n",
            "-IFD0:Make",
            "-ExifIFD:Make",
            "-Composite:Make",
            "-IFD0:Model",
            "-ExifIFD:Model",
            "-Composite:Model",
            "-ExifIFD:DateTimeOriginal",
            "-ExifIFD:CreateDate",
            "-QuickTime:CreateDate",
            "-XMP-exif:DateTimeOriginal",
            "-IFD0:ModifyDate",
            "-GPS:GPSLatitude",
            "-GPS:GPSLongitude",
            "-GPS:GPSAltitude",
            "-Composite:GPSLatitude",
            "-Composite:GPSLongitude",
            "-Composite:GPSAltitude"
        };
        var argFile = WriteArgsFile(photos.Select(p => p.Path));
        arguments.Add("-@"); arguments.Add(argFile);

        var result = await RunAsync(arguments, cancellationToken);
        try { File.Delete(argFile); } catch { }
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
        {
            throw new InvalidOperationException(result.Error);
        }

        using var document = JsonDocument.Parse(result.Output);
        var items = document.RootElement.EnumerateArray().ToArray();

        for (var index = 0; index < photos.Count && index < items.Length; index++)
        {
            ApplyMetadata(photos[index], items[index]);
        }
    }

    public async Task<IReadOnlyList<string>> WriteGpsAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        GpsCoordinate coordinate,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateWritableFiles(selectedPhotos);
        var targets = PrepareTargets(selectedPhotos, mode, outputDirectory, progress);
        if (targets.Count == 0)
        {
            return Array.Empty<string>();
        }

        progress?.Report(new WriteProgress("Writing GPS metadata with ExifTool...", null));
        var imageTargets = targets.Where(target => IsImageMetadataWrite(target.Path)).ToArray();
        var quickTimeTargets = targets.Where(target => IsQuickTimeVideoMetadataWrite(target.Path)).ToArray();

        if (imageTargets.Length > 0)
        {
            await WriteImageGpsAsync(imageTargets, coordinate, cancellationToken);
        }

        if (quickTimeTargets.Length > 0)
        {
            await WriteQuickTimeGpsAsync(quickTimeTargets, coordinate, cancellationToken);
        }

        var fujifilmGroups = imageTargets
            .Where(target => IsFujifilm(target.Photo) && NeedsMakeModelFix(target.Photo))
            .GroupBy(target => BuildFujifilmDisplayModel(target.Photo.CameraModel))
            .ToArray();
        if (fujifilmGroups.Length > 0)
        {
            await WriteFujifilmDisplayModelAsync(fujifilmGroups, progress, cancellationToken);
        }

        return targets.Select(target => target.Path).ToArray();
    }

    private async Task WriteImageGpsAsync(
        IReadOnlyList<WriteTarget> targets,
        GpsCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-overwrite_original",
            "-P",
            "-n",
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

        var gpsArgFile = WriteArgsFile(targets.Select(t => t.Path));
        arguments.Add("-@"); arguments.Add(gpsArgFile);
        var result = await RunAsync(arguments, cancellationToken);
        try { File.Delete(gpsArgFile); } catch { }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
    }

    private async Task WriteQuickTimeGpsAsync(
        IReadOnlyList<WriteTarget> targets,
        GpsCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-overwrite_original",
            "-P",
            $"-QuickTime:GPSCoordinates={FormatQuickTimeGps(coordinate)}"
        };

        var gpsArgFile = WriteArgsFile(targets.Select(t => t.Path));
        arguments.Add("-@"); arguments.Add(gpsArgFile);
        var result = await RunAsync(arguments, cancellationToken);
        try { File.Delete(gpsArgFile); } catch { }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
    }

    private static string FormatQuickTimeGps(GpsCoordinate coordinate)
    {
        var latitude = coordinate.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
        var longitude = coordinate.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
        return coordinate.Altitude.HasValue
            ? string.Join(", ", latitude, longitude, coordinate.Altitude.Value.ToString("0.#", CultureInfo.InvariantCulture))
            : string.Join(", ", latitude, longitude);
    }

    private static void ApplyMetadata(PhotoItem photo, JsonElement item)
    {
        photo.CameraMake = TryGetString(item, "IFD0:Make") ??
                           TryGetString(item, "ExifIFD:Make") ??
                           TryGetString(item, "Composite:Make");
        photo.CameraModel = TryGetString(item, "IFD0:Model") ??
                            TryGetString(item, "ExifIFD:Model") ??
                            TryGetString(item, "Composite:Model");
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

    private static string? TryGetString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;
    }

    private static double? TryGetDouble(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
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
                "-overwrite_original",
                "-P",
                "-Make=FUJIFILM",
                $"-Model={group.Key}"
            };

            var fujiArgFile = WriteArgsFile(group.Select(t => t.Path));
            arguments.Add("-@"); arguments.Add(fujiArgFile);

            var result = await RunAsync(arguments, cancellationToken);
            try { File.Delete(fujiArgFile); } catch { }
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
            }
        }
    }

    internal static bool IsFujifilm(PhotoItem photo)
    {
        return ContainsFujifilm(photo.CameraMake) ||
               ContainsFujifilm(photo.CameraModel) ||
               photo.FileName.StartsWith("DSCF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsFujifilm(string? value)
    {
        return value?.Contains("FUJIFILM", StringComparison.OrdinalIgnoreCase) == true ||
               value?.Contains("FUJI", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool NeedsMakeModelFix(PhotoItem photo)
    {
        var make = photo.CameraMake ?? "";
        var model = photo.CameraModel ?? "";
        return !ContainsFujifilm(make) || !model.Contains("FUJIFILM", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFujifilmDisplayModel(string? model)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? "X-M5" : model.Trim();
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
            : mode == WriteMode.DirectInPlace
                ? DirectInPlace(selectedPhotos, progress)
                : BackupInPlace(selectedPhotos, progress);
    }

    private static List<WriteTarget> CopyToOutputDirectory(
        IReadOnlyList<PhotoItem> selectedPhotos,
        string? outputDirectory,
        IProgress<WriteProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("Choose an output directory first.");
        }

        Directory.CreateDirectory(outputDirectory);
        var targets = new List<WriteTarget>();

        for (var index = 0; index < selectedPhotos.Count; index++)
        {
            var photo = selectedPhotos[index];
            var destination = GetUniquePath(Path.Combine(outputDirectory, photo.FileName));
            File.Copy(photo.Path, destination);
            File.SetCreationTime(destination, File.GetCreationTime(photo.Path));
            File.SetLastWriteTime(destination, File.GetLastWriteTime(photo.Path));
            targets.Add(new WriteTarget(photo, destination));
            photo.Status = $"Copied: {Path.GetFileName(destination)}";
            progress?.Report(new WriteProgress(
                $"Copying photos ({index + 1}/{selectedPhotos.Count})...",
                (index + 1) * 100d / selectedPhotos.Count));
        }

        return targets;
    }

    private static List<WriteTarget> BackupInPlace(
        IReadOnlyList<PhotoItem> selectedPhotos,
        IProgress<WriteProgress>? progress)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var targets = new List<WriteTarget>();

        for (var index = 0; index < selectedPhotos.Count; index++)
        {
            var photo = selectedPhotos[index];
            var backupDirectory = Path.Combine(photo.Directory, ".photo-info-backups", timestamp);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = GetUniquePath(Path.Combine(backupDirectory, photo.FileName));
            File.Copy(photo.Path, backupPath);
            targets.Add(new WriteTarget(photo, photo.Path));
            photo.Status = $"Backup: {backupPath}";
            progress?.Report(new WriteProgress(
                $"Creating backups ({index + 1}/{selectedPhotos.Count})...",
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
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public async Task WriteDateAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        string dateTimeOriginal,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateWritableFiles(selectedPhotos);
        var targets = PrepareTargets(selectedPhotos, mode, outputDirectory, progress);
        if (targets.Count == 0)
        {
            return;
        }

        progress?.Report(new WriteProgress("Writing date metadata with ExifTool...", null));
        var imageTargets = targets.Where(target => IsImageMetadataWrite(target.Path)).ToArray();
        var quickTimeTargets = targets.Where(target => IsQuickTimeVideoMetadataWrite(target.Path)).ToArray();

        if (imageTargets.Length > 0)
        {
            await WriteImageDateAsync(imageTargets, dateTimeOriginal, cancellationToken);
        }

        if (quickTimeTargets.Length > 0)
        {
            await WriteQuickTimeDateAsync(quickTimeTargets, dateTimeOriginal, cancellationToken);
        }

        foreach (var target in targets.Where(t => string.Equals(t.Photo.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
            target.Photo.DateTaken = dateTimeOriginal;
    }

    private async Task WriteImageDateAsync(
        IReadOnlyList<WriteTarget> targets,
        string dateTimeOriginal,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-overwrite_original",
            "-P",
            $"-DateTimeOriginal={dateTimeOriginal}",
            $"-CreateDate={dateTimeOriginal}",
            $"-ModifyDate={dateTimeOriginal}"
        };
        var dateArgFile = WriteArgsFile(targets.Select(t => t.Path));
        arguments.Add("-@"); arguments.Add(dateArgFile);
        var result = await RunAsync(arguments, cancellationToken);
        try { File.Delete(dateArgFile); } catch { }
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private async Task WriteQuickTimeDateAsync(
        IReadOnlyList<WriteTarget> targets,
        string dateTimeOriginal,
        CancellationToken cancellationToken)
    {
        var arguments = BuildQuickTimeDateArguments(dateTimeOriginal);
        var dateArgFile = WriteArgsFile(targets.Select(t => t.Path));
        arguments.Add("-@"); arguments.Add(dateArgFile);
        var result = await RunAsync(arguments, cancellationToken);
        try { File.Delete(dateArgFile); } catch { }
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    public async Task<int> WriteDateBatchAsync(
        IReadOnlyList<(PhotoItem Photo, string Date)> items,
        IProgress<WriteProgress>? progress,
        WriteMode writeMode,
        string? outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var total = items.Count;
        if (total == 0)
        {
            return 0;
        }

        var photos = items.Select(i => i.Photo).ToArray();
        ValidateWritableFiles(photos);
        var targets = PrepareTargets(photos, writeMode, outputDirectory, progress);
        if (targets.Count == 0)
        {
            return 0;
        }

        var argsFile = Path.GetTempFileName();
        var lines = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var dt = items[i].Date;
            lines.Add("-overwrite_original");
            lines.Add("-P");
            if (IsQuickTimeVideoMetadataWrite(targets[i].Path))
            {
                AddQuickTimeDateArguments(lines, dt);
            }
            else
            {
                lines.Add($"-DateTimeOriginal={dt}");
                lines.Add($"-CreateDate={dt}");
                lines.Add($"-ModifyDate={dt}");
            }
            lines.Add(targets[i].Path);
            lines.Add("-execute");
        }
        File.WriteAllLines(argsFile, lines, System.Text.Encoding.UTF8);

        progress?.Report(new WriteProgress("Writing checked dates with ExifTool...", null));
        var arguments = new List<string> { "-@", argsFile };
        var result = await RunAsync(arguments, cancellationToken);
        try { File.Delete(argsFile); } catch { }

        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

        var count = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (string.Equals(target.Photo.Path, target.Path, StringComparison.OrdinalIgnoreCase))
            {
                target.Photo.DateTaken = items[i].Date;
            }

            count++;
            progress?.Report(new WriteProgress(
                $"Date check written ({count}/{total})...",
                count * 100d / total));
        }
        return count;
    }

    private static string WriteArgsFile(IEnumerable<string> paths)
    {
        var tmpFile = Path.GetTempFileName();
        File.WriteAllLines(tmpFile, paths, System.Text.Encoding.UTF8);
        return tmpFile;
    }

    private static List<string> BuildQuickTimeDateArguments(string dateTimeOriginal)
    {
        var arguments = new List<string>
        {
            "-overwrite_original",
            "-P"
        };
        AddQuickTimeDateArguments(arguments, dateTimeOriginal);
        return arguments;
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

    private static void ValidateWritableFiles(IReadOnlyList<PhotoItem> photos)
    {
        var unsupported = photos
            .Where(photo => !IsSupportedMetadataWrite(photo.Path))
            .Select(photo => photo.FileName)
            .Take(5)
            .ToArray();

        if (unsupported.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Metadata writing is enabled for JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP and QuickTime videos (MP4/MOV/M4V/3GP). " +
            $"Skipped examples: {string.Join(", ", unsupported)}");
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_exifToolPath))
        {
            throw new FileNotFoundException("ExifTool was not found.", _exifToolPath);
        }

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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ExifTool.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record WriteTarget(PhotoItem Photo, string Path);
}

public sealed record WriteProgress(string Message, double? Percent);
