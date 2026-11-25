using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Encoder = System.Drawing.Imaging.Encoder;
using EncoderParameter = System.Drawing.Imaging.EncoderParameter;
using EncoderParameters = System.Drawing.Imaging.EncoderParameters;
using ImageCodecInfo = System.Drawing.Imaging.ImageCodecInfo;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public sealed class ImageConversionService
{
    private const int ExifOrientationTagId = 0x0112;
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
        ImageFormat.Avif => [ImageFormat.Jpeg, ImageFormat.Png],
        _ => []
    };

    public Task<IReadOnlyList<string>> ConvertAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        ImageFormat targetFormat,
        WriteMode mode,
        string? outputDirectory,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ConvertAsync(selectedPhotos, new ConversionOptions(targetFormat, mode, outputDirectory), progress, cancellationToken);

    public async Task<IReadOnlyList<string>> ConvertAsync(
        IReadOnlyList<PhotoItem> selectedPhotos,
        ConversionOptions options,
        IProgress<WriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentBag<string>();
        var total = selectedPhotos.Count;
        var completed = 0;

        if (options.WriteMode == WriteMode.CopyToOutputDirectory)
        {
            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
                throw new InvalidOperationException("Choose an output directory first.");
            Directory.CreateDirectory(options.OutputDirectory);
        }

        // Pre-compute deterministic output paths sequentially to avoid GetUniquePath race conditions
        var jobs = new List<(PhotoItem Photo, ImageFormat Source, string FinalPath, bool DirectSameExt)>();
        foreach (var photo in selectedPhotos)
        {
            var source = photo.DetectedFormat != ImageFormat.Unknown
                ? photo.DetectedFormat
                : FormatDetector.Detect(photo.Path);

            var convertible = GetConvertibleFormats(source);
            var allowResizeSameFormat = options.MaxDimension.HasValue && source == options.TargetFormat;
            if (!allowResizeSameFormat && (convertible.Length == 0 || !convertible.Contains(options.TargetFormat)))
            {
                photo.Status = $"Cannot convert: {FormatDetector.GetFormatLabel(source)}";
                continue;
            }

            var targetExt = FormatDetector.GetStandardExtension(options.TargetFormat);
            var outputName = Path.ChangeExtension(photo.FileName, targetExt.TrimStart('.'));

            if (options.WriteMode == WriteMode.CopyToOutputDirectory)
            {
                var finalPath = GetUniquePath(Path.Combine(options.OutputDirectory!, outputName));
                jobs.Add((photo, source, finalPath, false));
            }
            else
            {
                if (source == options.TargetFormat && !options.MaxDimension.HasValue)
                {
                    photo.Status = "Same format, skipped";
                    continue;
                }

                var sameExt = string.Equals(Path.GetExtension(photo.Path), Path.GetExtension(outputName), StringComparison.OrdinalIgnoreCase);
                var finalPath = sameExt ? photo.Path : GetUniquePath(Path.Combine(photo.Directory, outputName));
                jobs.Add((photo, source, finalPath, sameExt));
            }
        }

        var dop = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        await Parallel.ForEachAsync(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = cancellationToken
        }, async (job, ct) =>
        {
            var (photo, source, finalPath, sameExt) = job;
            if (options.WriteMode == WriteMode.CopyToOutputDirectory)
            {
                await ConvertOneWithMetadataAsync(photo.Path, source, finalPath, options, ct).ConfigureAwait(false);
                photo.Status = $"Converted: {Path.GetFileName(finalPath)}";
                results.Add(finalPath);
            }
            else
            {
                var targetExt = FormatDetector.GetStandardExtension(options.TargetFormat);
                var tempPath = Path.Combine(photo.Directory, $".{photo.FileName}.{Guid.NewGuid():N}.tmp{targetExt}");
                try
                {
                    await ConvertOneWithMetadataAsync(photo.Path, source, tempPath, options, ct).ConfigureAwait(false);
                    if (sameExt)
                    {
                        File.Replace(tempPath, finalPath, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        var oldPath = Path.Combine(photo.Directory, $".{photo.FileName}.{Guid.NewGuid():N}.old");
                        File.Move(photo.Path, oldPath);
                        try
                        {
                            File.Move(tempPath, finalPath);
                        }
                        catch
                        {
                            try { File.Move(oldPath, photo.Path); } catch { }
                            throw;
                        }
                        TryDeleteFile(oldPath);
                        photo.UpdatePath(finalPath);
                        photo.SetDetectedFormat(options.TargetFormat);
                    }
                    photo.Status = $"Converted: {Path.GetFileName(finalPath)}";
                    results.Add(finalPath);
                }
                finally
                {
                    TryDeleteFile(tempPath);
                }
            }

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new WriteProgress(
                $"Converted {done}/{jobs.Count} ({Path.GetFileName(finalPath)})",
                done * 100.0 / Math.Max(1, total)));
        }).ConfigureAwait(false);

        return results.ToArray();
    }

    private async Task ConvertOneWithMetadataAsync(
        string sourcePath,
        ImageFormat sourceFormat,
        string destinationPath,
        ConversionOptions options,
        CancellationToken cancellationToken)
    {
        if (sourceFormat == options.TargetFormat && !options.MaxDimension.HasValue)
        {
            await Task.Run(() =>
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
                PreserveFileTimes(sourcePath, destinationPath);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(() => ConvertPixels(sourcePath, destinationPath, options), cancellationToken).ConfigureAwait(false);

        try
        {
            await _exifTool.CopyMetadataFromAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Metadata copy failed for {destinationPath}: {ex.Message}");
        }

        PreserveFileTimes(sourcePath, destinationPath);
    }

    private static void ConvertPixels(string sourcePath, string destinationPath, ConversionOptions options)
    {
        var imgFormat = options.TargetFormat switch
        {
            ImageFormat.Jpeg => System.Drawing.Imaging.ImageFormat.Jpeg,
            ImageFormat.Png => System.Drawing.Imaging.ImageFormat.Png,
            ImageFormat.Bmp => System.Drawing.Imaging.ImageFormat.Bmp,
            ImageFormat.Gif => System.Drawing.Imaging.ImageFormat.Gif,
            ImageFormat.Tiff => System.Drawing.Imaging.ImageFormat.Tiff,
            _ => System.Drawing.Imaging.ImageFormat.Jpeg
        };

        using var loaded = Image.FromFile(sourcePath);
        ApplyExifOrientation(loaded);

        Image targetImage = loaded;
        Bitmap? resizedBitmap = null;
        if (options.MaxDimension is int maxDim && maxDim > 0 && (loaded.Width > maxDim || loaded.Height > maxDim))
        {
            var scale = Math.Min((double)maxDim / loaded.Width, (double)maxDim / loaded.Height);
            var w = Math.Max(1, (int)Math.Round(loaded.Width * scale));
            var h = Math.Max(1, (int)Math.Round(loaded.Height * scale));
            resizedBitmap = new Bitmap(w, h);
            using var g = Graphics.FromImage(resizedBitmap);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(loaded, 0, 0, w, h);
            targetImage = resizedBitmap;
        }

        try
        {
            if (options.TargetFormat == ImageFormat.Jpeg)
            {
                var encoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(codec => codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                if (encoder is not null)
                {
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(options.JpegQuality, 40, 100));
                    targetImage.Save(destinationPath, encoder, parameters);
                    return;
                }
            }

            targetImage.Save(destinationPath, imgFormat);
        }
        finally
        {
            resizedBitmap?.Dispose();
        }
    }

    private static void ApplyExifOrientation(Image image)
    {
        if (!image.PropertyIdList.Contains(ExifOrientationTagId))
            return;

        var prop = image.GetPropertyItem(ExifOrientationTagId);
        if (prop?.Value is not { Length: >= 2 })
            return;

        var orientation = BitConverter.ToUInt16(prop.Value, 0);
        var flip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };

        if (flip != RotateFlipType.RotateNoneFlipNone)
        {
            image.RotateFlip(flip);
            image.RemovePropertyItem(ExifOrientationTagId);
        }
    }

    private static void PreserveFileTimes(string sourcePath, string destinationPath)
    {
        try
        {
            File.SetCreationTime(destinationPath, File.GetCreationTime(sourcePath));
            File.SetLastWriteTime(destinationPath, File.GetLastWriteTime(sourcePath));
        }
        catch { }
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
        catch { }
    }
}
