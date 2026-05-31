using System.Drawing;
using System.IO;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public sealed class ImageConversionService
{
    private readonly ExifToolService _exifTool;

    public ImageConversionService(ExifToolService exifTool)
    {
        _exifTool = exifTool;
    }

    public static ImageFormat[] GetConvertibleFormats(ImageFormat source) => source switch
    {
        ImageFormat.Jpeg => [ImageFormat.Png, ImageFormat.Bmp, ImageFormat.Gif, ImageFormat.Tiff],
        ImageFormat.Png => [ImageFormat.Jpeg, ImageFormat.Bmp, ImageFormat.Gif, ImageFormat.Tiff],
        ImageFormat.Bmp => [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Gif, ImageFormat.Tiff],
        ImageFormat.Gif => [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Bmp, ImageFormat.Tiff],
        ImageFormat.Tiff => [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Bmp, ImageFormat.Gif],
        ImageFormat.Heic => [ImageFormat.Jpeg, ImageFormat.Png],
        ImageFormat.WebP => [ImageFormat.Jpeg, ImageFormat.Png],
        _ => []
    };

    public async Task<IReadOnlyList<string>> ConvertAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        ImageFormat targetFormat,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();

        for (var index = 0; index < selectedPhotos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var photo = selectedPhotos[index];
            var source = photo.DetectedFormat != ImageFormat.Unknown ? photo.DetectedFormat : FormatDetector.Detect(photo.Path);
            var targetExt = FormatDetector.GetStandardExtension(targetFormat);
            var outputName = Path.ChangeExtension(photo.FileName, targetExt.TrimStart('.'));
            string outputPath;

            progress?.Report(new WriteProgress($"Converting {photo.FileName} → {targetExt} ({index + 1}/{selectedPhotos.Count})...", (index + 1) * 100.0 / selectedPhotos.Count));

            switch (mode)
            {
                case WriteMode.CopyToOutputDirectory:
                {
                    if (string.IsNullOrWhiteSpace(outputDirectory))
                        throw new InvalidOperationException("Choose an output directory first.");
                    Directory.CreateDirectory(outputDirectory);
                    outputPath = GetUniquePath(Path.Combine(outputDirectory, outputName));
                    await ConvertOneAsync(photo.Path, source, outputPath, targetFormat, cancellationToken);
                    photo.Status = $"Converted: {outputName}";
                    results.Add(outputPath);
                    break;
                }
                case WriteMode.InPlaceWithBackup:
                {
                    var backupDir = Path.Combine(photo.Directory, ".photo-location-backups",
                        DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(backupDir);
                    var backupPath = GetUniquePath(Path.Combine(backupDir, photo.FileName));
                    File.Copy(photo.Path, backupPath);

                    outputPath = Path.Combine(photo.Directory, outputName);
                    await ConvertOneAsync(photo.Path, source, outputPath, targetFormat, cancellationToken);
                    photo.Status = $"Converted, backed up: {Path.GetFileName(backupPath)}";
                    results.Add(outputPath);
                    break;
                }
                case WriteMode.DirectInPlace:
                {
                    outputPath = Path.Combine(photo.Directory, outputName);
                    await ConvertOneAsync(photo.Path, source, outputPath, targetFormat, cancellationToken);
                    if (!photo.Path.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(photo.Path); } catch { /* original renamed, OK */ }
                    }
                    photo.Status = $"Converted: {outputName}";
                    results.Add(outputPath);
                    break;
                }
            }
        }

        return results;
    }

    private static async Task ConvertOneAsync(
        string sourcePath,
        ImageFormat sourceFormat,
        string destPath,
        ImageFormat targetFormat,
        CancellationToken cancellationToken)
    {
        if (sourceFormat == ImageFormat.Heic || sourceFormat == ImageFormat.WebP)
        {
            await ConvertViaExifToolAsync(sourcePath, destPath, targetFormat, cancellationToken);
        }
        else
        {
            ConvertViaDrawing(sourcePath, sourceFormat, destPath, targetFormat);
        }
    }

    private static void ConvertViaDrawing(
        string sourcePath,
        ImageFormat sourceFormat,
        string destPath,
        ImageFormat targetFormat)
    {
        if (sourceFormat == targetFormat)
        {
            File.Copy(sourcePath, destPath, overwrite: true);
            return;
        }

        var imgFormat = targetFormat switch
        {
            ImageFormat.Jpeg => System.Drawing.Imaging.ImageFormat.Jpeg,
            ImageFormat.Png => System.Drawing.Imaging.ImageFormat.Png,
            ImageFormat.Bmp => System.Drawing.Imaging.ImageFormat.Bmp,
            ImageFormat.Gif => System.Drawing.Imaging.ImageFormat.Gif,
            ImageFormat.Tiff => System.Drawing.Imaging.ImageFormat.Tiff,
            _ => System.Drawing.Imaging.ImageFormat.Jpeg
        };

        using var image = Image.FromFile(sourcePath);
        image.Save(destPath, imgFormat);

        File.SetCreationTime(destPath, File.GetCreationTime(sourcePath));
        File.SetLastWriteTime(destPath, File.GetLastWriteTime(sourcePath));
    }

    private static async Task ConvertViaExifToolAsync(
        string sourcePath,
        string destPath,
        ImageFormat targetFormat,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-o", destPath,
            "-n",
            sourcePath
        };

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ExifToolService.DefaultPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ExifTool for conversion.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ExifTool conversion failed: {error}");
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
