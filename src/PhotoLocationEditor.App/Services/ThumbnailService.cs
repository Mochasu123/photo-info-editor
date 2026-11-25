using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Encoder = System.Drawing.Imaging.Encoder;
using EncoderParameter = System.Drawing.Imaging.EncoderParameter;
using EncoderParameters = System.Drawing.Imaging.EncoderParameters;

namespace PhotoLocationEditor.App.Services;

/// <summary>
/// High-speed 2-tier (Memory LRU + LocalAppData Disk Cache) thumbnail and preview generator.
/// Uses Windows Shell IShellItemImageFactory for hardware-accelerated JPG/HEIC/RAW/Video thumbnails
/// with automatic EXIF orientation handling, falling back to ExifTool embedded previews & WIC decode.
/// </summary>
public sealed class ThumbnailService
{
    private const int MemoryCacheLimit = 260;
    private static readonly Guid IShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly ImageCodecInfo? JpegEncoder = ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);

    private readonly ExifToolService _exifTool;
    private readonly string _diskCacheDir;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, CacheEntry> _memCache = new(StringComparer.Ordinal);
    private long _accessTick;

    public ThumbnailService(ExifToolService exifTool)
    {
        _exifTool = exifTool;
        _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoInfoEditor",
            "ThumbCache");
        _concurrency = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 2, 8));
    }

    public async Task<byte[]?> GetThumbnailBytesAsync(
        string filePath,
        int maxPixelSize = 380,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        var clampedSize = Math.Clamp(maxPixelSize, 128, 2200);
        var key = BuildCacheKey(filePath, clampedSize);
        if (key is null)
            return null;

        if (_memCache.TryGetValue(key, out var hit))
        {
            hit.LastAccess = Interlocked.Increment(ref _accessTick);
            return hit.Bytes;
        }

        // Check disk cache for thumbnails <= 640px
        var diskPath = clampedSize <= 640 ? Path.Combine(_diskCacheDir, $"{key}.jpg") : null;
        if (diskPath is not null && File.Exists(diskPath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(diskPath, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    PutMemCache(key, bytes);
                    return bytes;
                }
            }
            catch { }
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_memCache.TryGetValue(key, out hit))
                return hit.Bytes;

            var bytes = await Task.Run(() => TryExtractShellThumbnail(filePath, clampedSize), cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                bytes = await TryExtractFallbackAsync(filePath, clampedSize, cancellationToken).ConfigureAwait(false);

            if (bytes is { Length: > 0 })
            {
                PutMemCache(key, bytes);
                if (diskPath is not null)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(_diskCacheDir);
                            File.WriteAllBytes(diskPath, bytes);
                        }
                        catch { }
                    }, CancellationToken.None);
                }
            }

            return bytes;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void InvalidateFile(string filePath)
    {
        foreach (var k in _memCache.Keys)
            _memCache.TryRemove(k, out _);
    }

    public void ClearMemoryCache() => _memCache.Clear();

    private void PutMemCache(string key, byte[] bytes)
    {
        if (_memCache.Count >= MemoryCacheLimit)
        {
            var victims = _memCache.OrderBy(kv => kv.Value.LastAccess).Take(MemoryCacheLimit / 3).Select(kv => kv.Key).ToArray();
            foreach (var v in victims)
                _memCache.TryRemove(v, out _);
        }
        _memCache[key] = new CacheEntry(bytes, Interlocked.Increment(ref _accessTick));
    }

    private static string? BuildCacheKey(string filePath, int size)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return null;
            var raw = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{size}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..24];
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> TryExtractFallbackAsync(string filePath, int size, CancellationToken cancellationToken)
    {
        var wic = await Task.Run(() => TryDecodeWithWic(filePath, size), cancellationToken).ConfigureAwait(false);
        if (wic is not null)
            return wic;

        var embedded = await _exifTool.ExtractEmbeddedPreviewAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (embedded is not null)
            return await Task.Run(() => ReencodeJpegFromBytes(embedded, size), cancellationToken).ConfigureAwait(false);

        return null;
    }

    private static byte[]? TryExtractShellThumbnail(string filePath, int size)
    {
        IShellItemImageFactory? factory = null;
        nint hBitmap = nint.Zero;
        try
        {
            var hr = SHCreateItemFromParsingName(filePath, nint.Zero, in IShellItemImageFactoryGuid, out factory);
            if (hr != 0 || factory is null)
                return null;

            // SIIGBF_RESIZETOFIT (0x00) | SIIGBF_BIGGERSIZEOK (0x01)
            const uint flags = 0x01;
            hr = factory.GetImage(new NativeSize { cx = size, cy = size }, flags, out hBitmap);
            if (hr != 0 || hBitmap == nint.Zero)
                return null;

            using var bmp = Image.FromHbitmap(hBitmap);
            return EncodeJpeg(bmp, 86L);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != nint.Zero)
                DeleteObject(hBitmap);
            if (factory is not null && Marshal.IsComObject(factory))
                Marshal.ReleaseComObject(factory);
        }
    }

    private static byte[]? TryDecodeWithWic(string filePath, int maxSize)
    {
        try
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.DecodePixelWidth = maxSize;
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(filePath);
            src.EndInit();
            src.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 86 };
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReencodeJpegFromBytes(byte[] rawBytes, int maxSize)
    {
        try
        {
            using var input = new MemoryStream(rawBytes);
            using var img = Image.FromStream(input);
            if (img.Width <= maxSize && img.Height <= maxSize)
                return rawBytes;

            var ratio = Math.Min((double)maxSize / img.Width, (double)maxSize / img.Height);
            var w = Math.Max(1, (int)Math.Round(img.Width * ratio));
            var h = Math.Max(1, (int)Math.Round(img.Height * ratio));
            using var resized = new Bitmap(w, h);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, w, h);
            }
            return EncodeJpeg(resized, 86L);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodeJpeg(Image image, long quality)
    {
        using var ms = new MemoryStream();
        if (JpegEncoder is not null)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            image.Save(ms, JpegEncoder, parameters);
        }
        else
        {
            image.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        return ms.ToArray();
    }

    private sealed class CacheEntry(byte[] bytes, long lastAccess)
    {
        public byte[] Bytes { get; } = bytes;
        public long LastAccess = lastAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out nint phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        nint pbc,
        in Guid riid,
        out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
