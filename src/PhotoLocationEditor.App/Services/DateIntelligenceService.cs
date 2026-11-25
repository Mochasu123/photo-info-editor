using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public static partial class DateIntelligenceService
{
    private static readonly DateTime MinValidDate = new(1995, 1, 1);

    public static bool TryParseExifDateTime(string? raw, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 10)
            return false;

        var trimmed = raw.Trim();
        if (trimmed.All(c => c is '0' or ':' or '-' or ' ' or 'T'))
            return false;

        string[] formats =
        [
            "yyyy:MM:dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy:MM:dd HH:mm",
            "yyyy-MM-dd HH:mm",
            "yyyy:MM:dd",
            "yyyy-MM-dd"
        ];

        var normalized = trimmed.Length >= 19 ? trimmed[..19] : trimmed;
        return DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
               || DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    public static string FormatExifDateTime(DateTime dt) =>
        dt.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string? ShiftExifTime(string? exifDate, TimeSpan delta)
    {
        if (!TryParseExifDateTime(exifDate, out var parsed))
            return null;
        return FormatExifDateTime(parsed + delta);
    }

    public static IReadOnlyList<(PhotoItem Photo, string Date)> GenerateSequence(
        IReadOnlyList<PhotoItem> photos,
        DateTime start,
        int stepSeconds)
    {
        var result = new List<(PhotoItem, string)>(photos.Count);
        for (var i = 0; i < photos.Count; i++)
        {
            var dt = start.AddSeconds((long)i * stepSeconds);
            result.Add((photos[i], FormatExifDateTime(dt)));
        }
        return result;
    }

    /// <summary>
    /// Extracts plausible capture timestamp from camera/app filenames such as:
    /// IMG_20250816_183012, Screenshot_2025-08-16-18-30-12, mmexport1723804212000, wx_camera_1723804212000.
    /// </summary>
    public static bool TryExtractFilenameTimestamp(string fileNameOrPath, out DateTime timestamp)
    {
        timestamp = default;
        var name = Path.GetFileNameWithoutExtension(fileNameOrPath);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Pattern 1: yyyyMMdd[_- ]HHmmss or yyyy-MM-dd[-_ ]HH[:.-]mm[:.-]ss
        var dateMatch = FullDateTimeFilenameRegex().Match(name);
        if (dateMatch.Success)
        {
            if (int.TryParse(dateMatch.Groups["y"].Value, out var y) &&
                int.TryParse(dateMatch.Groups["mo"].Value, out var mo) &&
                int.TryParse(dateMatch.Groups["d"].Value, out var d) &&
                int.TryParse(dateMatch.Groups["h"].Value, out var h) &&
                int.TryParse(dateMatch.Groups["mi"].Value, out var mi) &&
                int.TryParse(dateMatch.Groups["s"].Value, out var s))
            {
                try
                {
                    var candidate = new DateTime(y, mo, d, h, mi, s);
                    if (IsPlausiblePhotoDate(candidate))
                    {
                        timestamp = candidate;
                        return true;
                    }
                }
                catch { /* invalid calendar date like 20259999 */ }
            }
        }

        // Pattern 2: 13-digit ms Unix timestamp (WeChat mmexport / wx_camera / mobile exports)
        var msMatch = UnixMillisRegex().Match(name);
        if (msMatch.Success && long.TryParse(msMatch.Groups["ms"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            try
            {
                var candidate = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                if (IsPlausiblePhotoDate(candidate))
                {
                    timestamp = candidate;
                    return true;
                }
            }
            catch { }
        }

        // Pattern 3: 10-digit sec Unix timestamp
        var secMatch = UnixSecondsRegex().Match(name);
        if (secMatch.Success && long.TryParse(secMatch.Groups["sec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec))
        {
            try
            {
                var candidate = DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
                if (IsPlausiblePhotoDate(candidate))
                {
                    timestamp = candidate;
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    private static bool IsPlausiblePhotoDate(DateTime dt) =>
        dt >= MinValidDate && dt <= DateTime.Now.AddDays(2);

    public static List<DateDiagnosisItem> Diagnose(IReadOnlyList<PhotoItem> photos)
    {
        var items = new List<DateDiagnosisItem>(photos.Count);
        foreach (var p in photos)
        {
            DateTime? exifDt = null;
            var exifInvalid = false;
            if (!string.IsNullOrWhiteSpace(p.DateTaken))
            {
                if (TryParseExifDateTime(p.DateTaken, out var parsed))
                    exifDt = parsed;
                else
                    exifInvalid = true;
            }

            var hasFilenameTime = TryExtractFilenameTimestamp(p.FileName, out var fnDt);
            var created = p.FileCreationTime;
            DateTime modified;
            if (p.FileModificationTime != default)
            {
                modified = p.FileModificationTime;
            }
            else
            {
                try { modified = File.GetLastWriteTime(p.Path); }
                catch { modified = default; }
            }

            var cValid = created >= MinValidDate;
            var mValid = modified >= MinValidDate;
            DateTime? earliestFileDt = null;
            if (cValid && mValid) earliestFileDt = created < modified ? created : modified;
            else if (cValid) earliestFileDt = created;
            else if (mValid) earliestFileDt = modified;

            DateIssueCategory category;
            string reasonCode;
            var detailParts = new List<string>();
            DateTime? suggested = null;

            if (exifInvalid)
            {
                if (hasFilenameTime)
                {
                    category = DateIssueCategory.InvalidZeroExif;
                    reasonCode = "INVALID_EXIF_USE_FILENAME";
                    suggested = fnDt;
                    detailParts.Add("EXIF损坏/全零 → 可用文件名时间修复");
                }
                else if (earliestFileDt.HasValue)
                {
                    category = DateIssueCategory.InvalidZeroExif;
                    reasonCode = "INVALID_EXIF_USE_FILE";
                    suggested = earliestFileDt.Value;
                    detailParts.Add("EXIF损坏/全零 → 可用文件最早时间修复");
                }
                else
                {
                    category = DateIssueCategory.ManualNeeded;
                    reasonCode = "NO_USABLE_TIME";
                    detailParts.Add("EXIF损坏且无可用文件时间");
                }
            }
            else if (exifDt is null)
            {
                if (hasFilenameTime)
                {
                    category = DateIssueCategory.MissingExifWithFilenameTime;
                    reasonCode = "MISSING_EXIF_USE_FILENAME";
                    suggested = fnDt;
                    detailParts.Add("无EXIF → 从文件名精准还原");
                }
                else if (earliestFileDt.HasValue)
                {
                    category = DateIssueCategory.MissingExifWithFileTime;
                    reasonCode = "MISSING_EXIF_USE_FILE";
                    suggested = earliestFileDt.Value;
                    detailParts.Add("无EXIF → 建议使用文件最早时间");
                }
                else
                {
                    category = DateIssueCategory.ManualNeeded;
                    reasonCode = "NO_USABLE_TIME";
                    detailParts.Add("无EXIF且无可用时间");
                }
            }
            else if (earliestFileDt.HasValue && exifDt.Value > earliestFileDt.Value.AddMinutes(2))
            {
                // EXIF is noticeably later than the filesystem's earliest timestamp (e.g., edited/re-exported after copying)
                category = DateIssueCategory.ExifLaterThanFile;
                reasonCode = "EXIF_LATER_THAN_FILE";
                suggested = hasFilenameTime && fnDt < exifDt.Value ? fnDt : earliestFileDt.Value;
                detailParts.Add(hasFilenameTime && fnDt < exifDt.Value
                    ? "EXIF晚于文件名时间"
                    : "EXIF晚于文件最早时间");
            }
            else
            {
                category = DateIssueCategory.Consistent;
                reasonCode = "CONSISTENT";
                detailParts.Add("时间正常");
            }

            items.Add(new DateDiagnosisItem(
                p.Id,
                p.Path,
                p.FileName,
                exifDt.HasValue ? FormatExifDateTime(exifDt.Value) : (exifInvalid ? p.DateTaken : null),
                hasFilenameTime ? FormatExifDateTime(fnDt) : null,
                suggested.HasValue ? FormatExifDateTime(suggested.Value) : null,
                created,
                modified,
                category,
                reasonCode,
                string.Join(" · ", detailParts)));
        }
        return items;
    }

    [GeneratedRegex(@"(?<!\d)(?<y>(?:199\d|20[0-3]\d))[-_.]?(?<mo>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])[-_ T.]?(?<h>[01]\d|2[0-3])[-_.]?(?<mi>[0-5]\d)[-_.]?(?<s>[0-5]\d)(?!\d)")]
    private static partial Regex FullDateTimeFilenameRegex();

    [GeneratedRegex(@"(?<!\d)(?<ms>1[0-9]{12})(?!\d)")]
    private static partial Regex UnixMillisRegex();

    [GeneratedRegex(@"(?<!\d)(?<sec>1[0-9]{9})(?!\d)")]
    private static partial Regex UnixSecondsRegex();
}
