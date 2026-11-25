using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public static class GeoTrackService
{
    public static List<TrackPoint> ParseGpx(string gpxContentOrPath, bool isFilePath = true)
    {
        var doc = isFilePath ? XDocument.Load(gpxContentOrPath) : XDocument.Parse(gpxContentOrPath);
        var points = new List<TrackPoint>();

        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "trkpt" or "rtept" or "wpt"))
        {
            var latStr = el.Attribute("lat")?.Value;
            var lonStr = el.Attribute("lon")?.Value;
            if (!double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                continue;

            double? ele = null;
            var eleEl = el.Elements().FirstOrDefault(x => x.Name.LocalName == "ele");
            if (eleEl is not null && double.TryParse(eleEl.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEle))
                ele = parsedEle;

            var timeEl = el.Elements().FirstOrDefault(x => x.Name.LocalName == "time");
            if (timeEl is null || !DateTimeOffset.TryParse(timeEl.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                continue;

            points.Add(new TrackPoint(lat, lon, ele, dto.UtcDateTime));
        }

        points.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return points;
    }

    public static List<TrackMatchProposal> MatchPhotosToTrack(
        IReadOnlyList<PhotoItem> photos,
        IReadOnlyList<TrackPoint> trackPoints,
        TimeSpan maxTolerance,
        TimeSpan cameraTimezoneOffset)
    {
        var proposals = new List<TrackMatchProposal>();
        if (trackPoints.Count == 0)
            return proposals;

        foreach (var photo in photos)
        {
            if (!DateIntelligenceService.TryParseExifDateTime(photo.DateTaken, out var localDt))
                continue;

            var photoUtc = DateTime.SpecifyKind(localDt - cameraTimezoneOffset, DateTimeKind.Utc);
            var matched = InterpolateOrNearest(trackPoints, photoUtc, maxTolerance);
            if (matched is not null)
            {
                proposals.Add(new TrackMatchProposal(
                    photo.Id,
                    photo.FileName,
                    photo.DateTaken,
                    Math.Round(matched.Value.Latitude, 6),
                    Math.Round(matched.Value.Longitude, 6),
                    matched.Value.Altitude.HasValue ? Math.Round(matched.Value.Altitude.Value, 1) : null,
                    Math.Round(matched.Value.DeltaSeconds, 1),
                    matched.Value.Interpolated));
            }
        }

        return proposals;
    }

    private static (double Latitude, double Longitude, double? Altitude, double DeltaSeconds, bool Interpolated)?
        InterpolateOrNearest(IReadOnlyList<TrackPoint> sorted, DateTime targetUtc, TimeSpan maxTolerance)
    {
        if (sorted.Count == 0)
            return null;

        // Binary search
        var lo = 0;
        var hi = sorted.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid].TimestampUtc == targetUtc)
            {
                var p = sorted[mid];
                return (p.Latitude, p.Longitude, p.Elevation, 0, false);
            }
            if (sorted[mid].TimestampUtc < targetUtc) lo = mid + 1;
            else hi = mid - 1;
        }

        // Now hi is the last point <= targetUtc, lo is the first point > targetUtc
        if (hi >= 0 && lo < sorted.Count)
        {
            var before = sorted[hi];
            var after = sorted[lo];
            var span = (after.TimestampUtc - before.TimestampUtc).TotalSeconds;
            var dBefore = (targetUtc - before.TimestampUtc).TotalSeconds;
            var dAfter = (after.TimestampUtc - targetUtc).TotalSeconds;
            var nearestDelta = Math.Min(dBefore, dAfter);
            if (nearestDelta > maxTolerance.TotalSeconds)
                return null;

            if (span <= 1e-3)
                return (before.Latitude, before.Longitude, before.Elevation, dBefore, false);

            var t = dBefore / span;
            var lat = before.Latitude + (after.Latitude - before.Latitude) * t;
            var lon = before.Longitude + (after.Longitude - before.Longitude) * t;
            double? alt = before.Elevation.HasValue && after.Elevation.HasValue
                ? before.Elevation.Value + (after.Elevation.Value - before.Elevation.Value) * t
                : before.Elevation ?? after.Elevation;
            return (lat, lon, alt, nearestDelta, true);
        }

        var edge = hi >= 0 ? sorted[hi] : sorted[lo];
        var edgeDelta = Math.Abs((targetUtc - edge.TimestampUtc).TotalSeconds);
        return edgeDelta <= maxTolerance.TotalSeconds
            ? (edge.Latitude, edge.Longitude, edge.Elevation, edgeDelta, false)
            : null;
    }

    public static string ExportGpx(IEnumerable<PhotoItem> photos)
    {
        var geotagged = OrderedGeotagged(photos);
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<gpx version="1.1" creator="Photo Info Editor" xmlns="http://www.topografix.com/GPX/1/1">""");
        sb.AppendLine("  <trk><name>Photo Footprint</name><trkseg>");
        foreach (var p in geotagged)
        {
            var lat = p.Latitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
            var lon = p.Longitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
            sb.Append($"    <trkpt lat=\"{lat}\" lon=\"{lon}\">");
            if (p.Altitude.HasValue)
                sb.Append($"<ele>{p.Altitude.Value.ToString("0.#", CultureInfo.InvariantCulture)}</ele>");
            if (DateIntelligenceService.TryParseExifDateTime(p.DateTaken, out var dt))
                sb.Append($"<time>{dt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}</time>");
            sb.Append($"<name>{System.Security.SecurityElement.Escape(p.FileName)}</name>");
            sb.AppendLine("</trkpt>");
        }
        sb.AppendLine("  </trkseg></trk>");
        sb.AppendLine("</gpx>");
        return sb.ToString();
    }

    public static string ExportGeoJson(IEnumerable<PhotoItem> photos)
    {
        var geotagged = OrderedGeotagged(photos);
        var features = geotagged.Select(p => new
        {
            type = "Feature",
            geometry = new
            {
                type = "Point",
                coordinates = p.Altitude.HasValue
                    ? new[] { p.Longitude!.Value, p.Latitude!.Value, p.Altitude.Value }
                    : new[] { p.Longitude!.Value, p.Latitude!.Value }
            },
            properties = new
            {
                fileName = p.FileName,
                dateTaken = p.DateTaken,
                camera = p.CameraDisplay,
                format = p.FormatDisplay
            }
        });

        var collection = new { type = "FeatureCollection", features };
        return JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ExportCsv(IEnumerable<PhotoItem> photos)
    {
        var geotagged = OrderedGeotagged(photos);
        var sb = new StringBuilder();
        sb.AppendLine("FileName,DateTaken,Latitude,Longitude,Altitude,Camera,Format,Path");
        foreach (var p in geotagged)
        {
            sb.Append(CsvEscape(p.FileName)).Append(',')
              .Append(CsvEscape(p.DateTaken ?? "")).Append(',')
              .Append(p.Latitude!.Value.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Longitude!.Value.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Altitude?.ToString("0.#", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(CsvEscape(p.CameraDisplay)).Append(',')
              .Append(CsvEscape(p.FormatDisplay)).Append(',')
              .AppendLine(CsvEscape(p.Path));
        }
        return sb.ToString();
    }

    private static List<PhotoItem> OrderedGeotagged(IEnumerable<PhotoItem> photos) =>
        photos.Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
              .OrderBy(p => p.DateTaken ?? "9999", StringComparer.Ordinal)
              .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
              .ToList();

    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny([',', '"', '\n', '\r']) < 0)
            return s;
        return $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
