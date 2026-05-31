using System.IO;

namespace PhotoLocationEditor.App.Services;

public enum ImageFormat
{
    Unknown,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    Heic,
    WebP
}

public static class FormatDetector
{
    public static ImageFormat Detect(string path)
    {
        if (!File.Exists(path)) return ImageFormat.Unknown;

        var header = new byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header, 0, header.Length);
        if (read < 4) return ImageFormat.Unknown;

        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ImageFormat.Jpeg;

        // PNG: 89 50 4E 47
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return ImageFormat.Png;

        // GIF: 47 49 46 38
        if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
            return ImageFormat.Gif;

        // BMP: 42 4D
        if (header[0] == 0x42 && header[1] == 0x4D)
            return ImageFormat.Bmp;

        // TIFF LE: 49 49 2A 00
        if (header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
            return ImageFormat.Tiff;

        // TIFF BE: 4D 4D 00 2A
        if (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A)
            return ImageFormat.Tiff;

        // WebP: 52 49 46 46 ... 57 45 42 50
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            read >= 12 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            return ImageFormat.WebP;

        // HEIC/HEIF: check ftyp box at offset 4-11
        if (read >= 12 &&
            header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
        {
            // Check for heic/heif/heix/hevc/mif1
            var ftyp = System.Text.Encoding.ASCII.GetString(header, 8, 4).ToLowerInvariant();
            if (ftyp is "heic" or "heif" or "heix" or "hevc" or "mif1")
                return ImageFormat.Heic;
        }

        return ImageFormat.Unknown;
    }

    public static string GetStandardExtension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tiff",
        ImageFormat.Heic => ".heic",
        ImageFormat.WebP => ".webp",
        _ => string.Empty
    };

    public static string GetFormatLabel(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Png => "PNG",
        ImageFormat.Gif => "GIF",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Tiff => "TIFF",
        ImageFormat.Heic => "HEIC",
        ImageFormat.WebP => "WebP",
        _ => "?"
    };
}
