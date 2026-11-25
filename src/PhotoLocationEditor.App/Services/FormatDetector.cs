using System.IO;
using System.Text;

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
    Avif,
    Dng,
    Cr2,
    Cr3,
    Nef,
    Arw,
    Raf,
    Mp4,
    Mov,
    Avi,
    Mkv,
    Mts,
    Wmv
}

public static class FormatDetector
{
    public static ImageFormat Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ImageFormat.Unknown;

        Span<byte> header = stackalloc byte[384];
        int read;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = RandomAccess.Read(handle, header, 0);
        }
        catch
        {
            return ImageFormat.Unknown;
        }

        if (read < 4)
            return ImageFormat.Unknown;

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

        // Fujifilm RAF: "FUJIFILMCCD-RAW"
        if (read >= 15 && Encoding.ASCII.GetString(header[..15]) == "FUJIFILMCCD-RAW")
            return ImageFormat.Raf;

        // Canon CR2: TIFF LE + "CR\x02\x00" at offset 8
        if (read >= 12 &&
            header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00 &&
            header[8] == 0x43 && header[9] == 0x52 && header[10] == 0x02)
            return ImageFormat.Cr2;

        // TIFF-based formats (TIFF / DNG / NEF / ARW by extension disambiguation when magic is TIFF)
        if ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
            (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A))
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".dng" => ImageFormat.Dng,
                ".nef" => ImageFormat.Nef,
                ".arw" => ImageFormat.Arw,
                ".cr2" => ImageFormat.Cr2,
                _ => ImageFormat.Tiff
            };
        }

        // WebP: RIFF....WEBP
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            read >= 12 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            return ImageFormat.WebP;

        // ISO Base Media File Format (ftyp box): HEIC / AVIF / CR3 / MP4 / MOV / M4V / 3GP
        if (read >= 12 &&
            header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
        {
            var ftyp = Encoding.ASCII.GetString(header.Slice(8, 4)).ToLowerInvariant();
            if (ftyp is "heic" or "heif" or "heix" or "hevc" or "mif1")
                return ImageFormat.Heic;
            if (ftyp is "avif" or "avis")
                return ImageFormat.Avif;
            if (ftyp is "crx ")
                return ImageFormat.Cr3;
            if (ftyp is "qt  " or "mqv ")
                return ImageFormat.Mov;
            if (ftyp is "mp41" or "mp42" or "isom" or "avc1" or "m4v " or "mmp4" or "msnv" || ftyp.StartsWith("3g", StringComparison.Ordinal))
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
            return ImageFormat.Wmv;

        // MTS/M2TS: MPEG transport stream sync byte 0x47 at 188-byte stride (or +4 byte timestamp prefix)
        if (LooksLikeTransportStream(header, read, 0) || LooksLikeTransportStream(header, read, 4))
            return ImageFormat.Mts;

        return ImageFormat.Unknown;
    }

    private static bool LooksLikeTransportStream(ReadOnlySpan<byte> header, int read, int offset) =>
        read > offset + 188 && header[offset] == 0x47 && header[offset + 188] == 0x47;

    public static bool IsVideoFormat(ImageFormat format) =>
        format is ImageFormat.Mp4 or ImageFormat.Mov or ImageFormat.Avi or ImageFormat.Mkv or ImageFormat.Mts or ImageFormat.Wmv;

    public static bool IsRawFormat(ImageFormat format) =>
        format is ImageFormat.Dng or ImageFormat.Cr2 or ImageFormat.Cr3 or ImageFormat.Nef or ImageFormat.Arw or ImageFormat.Raf;

    public static string GetStandardExtension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tiff",
        ImageFormat.Heic => ".heic",
        ImageFormat.WebP => ".webp",
        ImageFormat.Avif => ".avif",
        ImageFormat.Dng => ".dng",
        ImageFormat.Cr2 => ".cr2",
        ImageFormat.Cr3 => ".cr3",
        ImageFormat.Nef => ".nef",
        ImageFormat.Arw => ".arw",
        ImageFormat.Raf => ".raf",
        ImageFormat.Mp4 => ".mp4",
        ImageFormat.Mov => ".mov",
        ImageFormat.Avi => ".avi",
        ImageFormat.Mkv => ".mkv",
        ImageFormat.Mts => ".mts",
        ImageFormat.Wmv => ".wmv",
        _ => string.Empty
    };

    public static bool IsExtensionValid(string path, ImageFormat format)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return format switch
        {
            ImageFormat.Jpeg => extension is ".jpg" or ".jpeg",
            ImageFormat.Tiff => extension is ".tif" or ".tiff",
            ImageFormat.Heic => extension is ".heic" or ".heif" or ".hif",
            ImageFormat.Mp4 => extension is ".mp4" or ".m4v" or ".3gp",
            ImageFormat.Mts => extension is ".mts" or ".m2ts",
            _ => extension == GetStandardExtension(format)
        };
    }

    public static string GetFormatLabel(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Png => "PNG",
        ImageFormat.Gif => "GIF",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Tiff => "TIFF",
        ImageFormat.Heic => "HEIC",
        ImageFormat.WebP => "WebP",
        ImageFormat.Avif => "AVIF",
        ImageFormat.Dng => "DNG",
        ImageFormat.Cr2 => "CR2",
        ImageFormat.Cr3 => "CR3",
        ImageFormat.Nef => "NEF",
        ImageFormat.Arw => "ARW",
        ImageFormat.Raf => "RAF",
        ImageFormat.Mp4 => "MP4",
        ImageFormat.Mov => "MOV",
        ImageFormat.Avi => "AVI",
        ImageFormat.Mkv => "MKV",
        ImageFormat.Mts => "MTS",
        ImageFormat.Wmv => "WMV",
        _ => "?"
    };
}
