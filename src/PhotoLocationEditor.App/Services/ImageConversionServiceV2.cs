using System.Drawing;
using System.IO;
using Encoder = System.Drawing.Imaging.Encoder;
using EncoderParameter = System.Drawing.Imaging.EncoderParameter;
using EncoderParameters = System.Drawing.Imaging.EncoderParameters;
using ImageCodecInfo = System.Drawing.Imaging.ImageCodecInfo;
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
            var source = photo.DetectedFormat != ImageFormat.Unknown
                ? photo.DetectedFormat
                : FormatDetector.Detect(photo.Path);

            var convertible = GetConvertibleFormats(source);
            if (convertible.Length == 0 || !convertible.Contains(targetFormat))
            {
                photo.Status = $"Cannot convert: {FormatDetector.GetFormatLabel(source)}";
                continue;
            }

            var targetExt = FormatDetector.GetStandardExtension(targetFormat);
            var outputName = Path.ChangeExtension(photo.FileName, targetExt.TrimStart('.'));
            string? finalPath = null;

            progress?.Report(new WriteProgress(
                $"Converting {photo.FileName} → {targetExt} ({index + 1}/{selectedPhotos.Count})...",
                (index + 1) * 100.0 / selectedPhotos.Count));

            switch (mode)
            {
                case WriteMode.CopyToOutputDirectory:
                {
                    if (string.IsNullOrWhiteSpace(outputDirectory))
                        throw new InvalidOperationException("Choose an output directory first.");

                    Directory.CreateDirectory(outputDirectory);
                    finalPath = GetUniquePath(Path.Combine(outputDirectory, outputName));
                    await ConvertOneWithMetadataAsync(photo.Path, source, finalPath, targetFormat, cancellationToken);
                    photo.Status = $"Converted: {Path.GetFileName(finalPath)}";
                    results.Add(finalPath);
                    break;
                }

                case WriteMode.DirectInPlace:
                {
                    // Never delete the original before the converted file is fully written.
                    var sameExtension = string.Equals(
                        Path.GetExtension(photo.Path),
                        Path.GetExtension(outputName),
                        StringComparison.OrdinalIgnoreCase);
                    finalPath = sameExtension
                        ? photo.Path
                        : GetUniquePath(Path.Combine(photo.Directory, outputName));

                    if (source == targetFormat)
                    {
                        // Already the requested format: copy-mode handles copies, direct mode is a no-op.
                        photo.Status = "Same format, skipped";
                        break;
                    }

                    var tempPath = Path.Combine(
                        photo.Directory,
                        $".{photo.FileName}.{Guid.NewGuid():N}.tmp{targetExt}");
                    try
                    {
                        await ConvertOneWithMetadataAsync(photo.Path, source, tempPath, targetFormat, cancellationToken);

                        if (sameExtension)
                        {
                            File.Replace(tempPath, finalPath, null, ignoreMetadataErrors: true);
                        }
                        else
                        {
                            var oldPath = Path.Combine(
                                photo.Directory,
                                $".{photo.FileName}.{Guid.NewGuid():N}.old");
                            File.Move(photo.Path, oldPath);
                            try
                            {
                                File.Move(tempPath, finalPath);
                            }
                            catch
                            {
                                try { File.Move(oldPath, photo.Path); } catch { /* rollback best effort */ }
                                throw;
                            }

                            TryDeleteFile(oldPath);
                            photo.UpdatePath(finalPath);
                            photo.SetDetectedFormat(targetFormat);
                        }

                        photo.Status = $"Converted: {Path.GetFileName(finalPath)}";
                        results.Add(finalPath);
                    }
                    finally
                    {
                        TryDeleteFile(tempPath);
                    }

                    break;
                }

                default:
                    throw new InvalidOperationException($"Unsupported write mode: {mode}");
            }
        }

        return results;
    }

    private async Task ConvertOneWithMetadataAsync(
        string sourcePath,
        ImageFormat sourceFormat,
        string destinationPath,
        ImageFormat targetFormat,
        CancellationToken cancellationToken)
    {
        if (sourceFormat == targetFormat)
        {
            await Task.Run(() =>
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
                PreserveFileTimes(sourcePath, destinationPath);
            }, cancellationToken);
            return;
        }

        // Decode/encode off the UI thread.
        await Task.Run(
            () => ConvertPixels(sourcePath, destinationPath, targetFormat),
            cancellationToken);

        // System.Drawing does not keep EXIF/GPS; copy metadata back with ExifTool.
        try
        {
            await _exifTool.CopyMetadataFromAsync(sourcePath, destinationPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The image is still valid; only metadata copying failed.
            System.Diagnostics.Debug.WriteLine($"Metadata copy failed for {destinationPath}: {ex.Message}");
        }

        PreserveFileTimes(sourcePath, destinationPath);
    }

    private static void ConvertPixels(string sourcePath, string destinationPath, ImageFormat targetFormat)
    {
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
        if (targetFormat == ImageFormat.Jpeg && imgFormat == System.Drawing.Imaging.ImageFormat.Jpeg)
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(codec => codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            if (encoder is not null)
            {
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                image.Save(destinationPath, encoder, parameters);
                return;
            }
        }

        image.Save(destinationPath, imgFormat);
    }

    private static void PreserveFileTimes(string sourcePath, string destinationPath)
    {
        try
        {
            File.SetCreationTime(destinationPath, File.GetCreationTime(sourcePath));
            File.SetLastWriteTime(destinationPath, File.GetLastWriteTime(sourcePath));
        }
        catch
        {
            // best effort
        }
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
