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
    WebP,
    Mp4,
    Mov,
    Avi,
    Mkv
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

        // ftyp box: HEIC/HEIF/MP4/MOV/M4V/3GP
        if (read >= 12 &&
            header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
        {
            var ftyp = System.Text.Encoding.ASCII.GetString(header, 8, 4).ToLowerInvariant();
            if (ftyp is "heic" or "heif" or "heix" or "hevc" or "mif1")
                return ImageFormat.Heic;
            if (ftyp is "mp41" or "mp42" or "isom" or "avc1" or "m4v " or "3gp4" or "3gp5" or "mmp4" or "msnv")
                return ImageFormat.Mp4;
            if (ftyp is "qt  " or "mqv ")
                return ImageFormat.Mov;
            if (ftyp is "3gp6" or "3gp5" or "3gp4" || ftyp.StartsWith("3g"))
                return ImageFormat.Mp4;
        }

        // AVI: RIFF....AVI
        if (read >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x41 && header[9] == 0x56 && header[10] == 0x49 && header[11] == 0x20)
            return ImageFormat.Avi;

        // MKV: EBML header 1A 45 DF A3
        if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
            return ImageFormat.Mkv;

        // WMV/ASF: 30 26 B2 75
        if (header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75)
            return ImageFormat.Avi; // treat as AVI-like container

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
        ImageFormat.Mp4 => ".mp4",
        ImageFormat.Mov => ".mov",
        ImageFormat.Avi => ".avi",
        ImageFormat.Mkv => ".mkv",
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
        ImageFormat.Mp4 => "MP4",
        ImageFormat.Mov => "MOV",
        ImageFormat.Avi => "AVI",
        ImageFormat.Mkv => "MKV",
        _ => "?"
    };
}
