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
        return IsImageMetadataWrite(extension) || IsQuickTimeVideoMetadataWrite(extension);
    }

    private static bool IsImageMetadataWrite(string extension) =>
        extension is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".hif" or ".png" or ".webp";

    private static bool IsQuickTimeVideoMetadataWrite(string extension) =>
        extension is ".mp4" or ".mov" or ".m4v" or ".3gp";

    public async Task ReadMetadataAsync(IReadOnlyList<PhotoItem> photos, CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0)
            return;

        var arguments = new List<string>
        {
            "-j", "-a", "-G1", "-s", "-n",
            "-IFD0:Make", "-ExifIFD:Make", "-Composite:Make",
            "-IFD0:Model", "-ExifIFD:Model", "-Composite:Model",
            "-ExifIFD:DateTimeOriginal", "-ExifIFD:CreateDate",
            "-QuickTime:CreateDate", "-XMP-exif:DateTimeOriginal",
            "-IFD0:ModifyDate",
            "-GPS:GPSLatitude", "-GPS:GPSLongitude", "-GPS:GPSAltitude",
            "-Composite:GPSLatitude", "-Composite:GPSLongitude", "-Composite:GPSAltitude"
        };

        var argFile = WriteArgsFile(photos.Select(p => p.Path));
        arguments.Add("-@");
        arguments.Add(argFile);

        try
        {
            var result = await RunAsync(arguments, cancellationToken);
            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException(result.Error);

            var output = result.Output;
            await Task.Run(() =>
            {
                using var document = JsonDocument.Parse(output);
                var items = document.RootElement.EnumerateArray().ToArray();

                for (var index = 0; index < photos.Count && index < items.Length; index++)
                    ApplyMetadata(photos[index], items[index]);
            });
        }
        finally
        {
            TryDeleteFile(argFile);
        }
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
            ? await Task.Run(() => PrepareTargets(plan.Writable, mode, outputDirectory, progress), cancellationToken)
            : PrepareTargets(plan.Writable, mode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), plan.Skipped);

        progress?.Report(new WriteProgress("Writing GPS metadata with ExifTool...", null));

        var imageTargets = targets.Where(t => IsImageMetadataWrite(Path.GetExtension(t.Path).ToLowerInvariant())).ToArray();
        var quickTimeTargets = targets.Where(t => IsQuickTimeVideoMetadataWrite(Path.GetExtension(t.Path).ToLowerInvariant())).ToArray();

        if (imageTargets.Length > 0)
            await WriteImageGpsAsync(imageTargets, coordinate, cancellationToken);

        if (quickTimeTargets.Length > 0)
            await WriteQuickTimeGpsAsync(quickTimeTargets, coordinate, cancellationToken);

        var fujifilmGroups = imageTargets
            .Where(target => IsFujifilm(target.Photo) && NeedsMakeModelFix(target.Photo))
            .GroupBy(target => BuildFujifilmDisplayModel(target.Photo.CameraModel))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToArray();

        if (fujifilmGroups.Length > 0)
            await WriteFujifilmDisplayModelAsync(fujifilmGroups, progress, cancellationToken);

        return new WriteResult(targets.Select(target => target.Path).ToArray(), plan.Skipped);
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

        await RunForTargetsAsync(arguments, targets, cancellationToken);
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

        await RunForTargetsAsync(arguments, targets, cancellationToken);
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
        var arguments = new List<string>(baseArguments);
        var argFile = WriteArgsFile(targets.Select(t => t.Path));
        arguments.Add("-@");
        arguments.Add(argFile);

        try
        {
            var result = await RunAsync(arguments, cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
        finally
        {
            TryDeleteFile(argFile);
        }
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
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
            return value;

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
                "-overwrite_original", "-P",
                "-Make=FUJIFILM",
                $"-Model={group.Key}"
            };

            await RunForTargetsAsync(arguments, group.ToArray(), cancellationToken);
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
        // Do not invent a model name when metadata is missing.
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
            catch
            {
                // Timestamp preservation is best-effort.
            }

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
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = FilterWritable(selectedPhotos);
        if (plan.Writable.Count == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(plan.Skipped));

        var targets = mode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(plan.Writable, mode, outputDirectory, progress), cancellationToken)
            : PrepareTargets(plan.Writable, mode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), plan.Skipped);

        progress?.Report(new WriteProgress("Writing date metadata with ExifTool...", null));
        var imageTargets = targets.Where(t => IsImageMetadataWrite(Path.GetExtension(t.Path).ToLowerInvariant())).ToArray();
        var quickTimeTargets = targets.Where(t => IsQuickTimeVideoMetadataWrite(Path.GetExtension(t.Path).ToLowerInvariant())).ToArray();

        if (imageTargets.Length > 0)
            await WriteImageDateAsync(imageTargets, dateTimeOriginal, cancellationToken);

        if (quickTimeTargets.Length > 0)
            await WriteQuickTimeDateAsync(quickTimeTargets, dateTimeOriginal, cancellationToken);

        foreach (var target in targets.Where(t => string.Equals(t.Photo.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
            target.Photo.DateTaken = dateTimeOriginal;

        return new WriteResult(targets.Select(t => t.Path).ToArray(), plan.Skipped);
    }

    private async Task WriteImageDateAsync(
        IReadOnlyList<WriteTarget> targets,
        string dateTimeOriginal,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-overwrite_original", "-P",
            $"-DateTimeOriginal={dateTimeOriginal}",
            $"-CreateDate={dateTimeOriginal}",
            $"-ModifyDate={dateTimeOriginal}"
        };

        await RunForTargetsAsync(arguments, targets, cancellationToken);
    }

    private async Task WriteQuickTimeDateAsync(
        IReadOnlyList<WriteTarget> targets,
        string dateTimeOriginal,
        CancellationToken cancellationToken)
    {
        var arguments = BuildQuickTimeDateArguments(dateTimeOriginal);
        await RunForTargetsAsync(arguments, targets, cancellationToken);
    }

    public async Task<WriteResult> WriteDateBatchAsync(
        IReadOnlyList<(PhotoItem Photo, string Date)> items,
        IProgress<WriteProgress>? progress,
        WriteMode writeMode,
        string? outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return new WriteResult(Array.Empty<string>(), Array.Empty<string>());

        var writableItems = items
            .Where(i => IsSupportedMetadataWrite(i.Photo.Path))
            .ToArray();
        var skipped = items
            .Where(i => !IsSupportedMetadataWrite(i.Photo.Path))
            .Select(i => i.Photo.FileName)
            .ToArray();

        if (writableItems.Length == 0)
            throw new InvalidOperationException(BuildUnsupportedMessage(skipped));

        var photos = writableItems.Select(i => i.Photo).ToArray();
        var targets = writeMode == WriteMode.CopyToOutputDirectory
            ? await Task.Run(() => PrepareTargets(photos, writeMode, outputDirectory, progress), cancellationToken)
            : PrepareTargets(photos, writeMode, outputDirectory, progress);
        if (targets.Count == 0)
            return new WriteResult(Array.Empty<string>(), skipped);

        var argsFile = Path.GetTempFileName();
        try
        {
            var lines = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
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
                lines.Add("-execute");
            }

            File.WriteAllLines(argsFile, lines, Encoding.UTF8);

            progress?.Report(new WriteProgress("Writing checked dates with ExifTool...", null));
            var result = await RunAsync(new List<string> { "-@", argsFile }, cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);

            var count = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (string.Equals(target.Photo.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                    target.Photo.DateTaken = writableItems[i].Date;

                count++;
                progress?.Report(new WriteProgress(
                    $"Date check written ({count}/{targets.Count})...",
                    count * 100d / targets.Count));
            }

            return new WriteResult(targets.Select(t => t.Path).ToArray(), skipped);
        }
        finally
        {
            TryDeleteFile(argsFile);
        }
    }

    private static string WriteArgsFile(IEnumerable<string> paths)
    {
        var tmpFile = Path.GetTempFileName();
        File.WriteAllLines(tmpFile, paths, Encoding.UTF8);
        return tmpFile;
    }

    private static List<string> BuildQuickTimeDateArguments(string dateTimeOriginal)
    {
        var arguments = new List<string> { "-overwrite_original", "-P" };
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

    private static (IReadOnlyList<PhotoItem> Writable, string[] Skipped) FilterWritable(
        IReadOnlyList<PhotoItem> photos)
    {
        var writable = photos.Where(p => IsSupportedMetadataWrite(p.Path)).ToArray();
        var skipped = photos.Where(p => !IsSupportedMetadataWrite(p.Path))
            .Select(p => p.FileName)
            .ToArray();
        return (writable, skipped);
    }

    private static string BuildUnsupportedMessage(IReadOnlyList<string> skipped)
    {
        var examples = string.Join(", ", skipped.Take(5));
        var more = skipped.Count > 5 ? $" (+{skipped.Count - 5})" : "";
        return "Metadata writing is enabled for JPG/JPEG/HEIC/HEIF/HIF/PNG/WebP and QuickTime videos (MP4/MOV/M4V/3GP). " +
               $"Skipped {skipped.Count} read-only file(s): {examples}{more}";
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_exifToolPath))
            throw new FileNotFoundException("ExifTool was not found.", _exifToolPath);

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
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ExifTool.");

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may already have exited */ }
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
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

        var result = await RunAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private sealed record WriteTarget(PhotoItem Photo, string Path);
}

public sealed record WriteResult(
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> SkippedFileNames);

public sealed record WriteProgress(string Message, double? Percent);
