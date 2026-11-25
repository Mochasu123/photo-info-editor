using System.Globalization;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

/// <summary>
/// Computes solar elevation, azimuth, and natural light phase (Golden Hour, Blue Hour, etc.)
/// using NOAA/Jean Meeus low-precision solar position equations.
/// </summary>
public static class SolarCalculator
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    public static SolarInfo Compute(double latitude, double longitude, DateTime localOrTaggedDateTime, string? offsetTime = null)
    {
        var utc = ToUtc(localOrTaggedDateTime, longitude, offsetTime);
        var jd = ToJulianDate(utc);
        var n = jd - 2451545.0; // days since J2000.0

        // Mean longitude & mean anomaly (degrees)
        var l = NormalizeDeg(280.460 + 0.9856474 * n);
        var g = NormalizeDeg(357.528 + 0.9856003 * n) * DegToRad;

        // Ecliptic longitude
        var lambda = (l + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * DegToRad;
        var epsilon = (23.439 - 0.0000004 * n) * DegToRad;

        // Declination & Equation of Time
        var sinDec = Math.Sin(epsilon) * Math.Sin(lambda);
        var dec = Math.Asin(sinDec);

        var ra = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda)) * RadToDeg;
        ra = NormalizeDeg(ra);

        // Greenwich Mean Sidereal Time (degrees)
        var gmst = NormalizeDeg(280.46061837 + 360.98564736629 * n);
        var lmst = gmst + longitude;
        var hourAngle = (lmst - ra) * DegToRad;

        var latRad = latitude * DegToRad;
        var sinAlt = Math.Sin(latRad) * Math.Sin(dec) + Math.Cos(latRad) * Math.Cos(dec) * Math.Cos(hourAngle);
        var elevation = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * RadToDeg;

        var cosAz = (Math.Sin(dec) - Math.Sin(latRad) * sinAlt) /
                    Math.Max(1e-9, Math.Cos(latRad) * Math.Cos(elevation * DegToRad));
        var azimuth = Math.Acos(Math.Clamp(cosAz, -1.0, 1.0)) * RadToDeg;
        if (Math.Sin(hourAngle) > 0)
            azimuth = 360.0 - azimuth;

        var phase = elevation switch
        {
            >= 6.0 => "Daylight",
            >= -4.0 => "GoldenHour",
            >= -6.0 => "BlueHour",
            >= -12.0 => "CivilTwilight",
            _ => "Night"
        };

        return new SolarInfo(
            Math.Round(elevation, 1),
            Math.Round(azimuth, 1),
            phase,
            ToCompass(azimuth));
    }

    private static DateTime ToUtc(DateTime dt, double longitude, string? offsetTime)
    {
        if (!string.IsNullOrWhiteSpace(offsetTime) && TryParseOffset(offsetTime, out var offset))
            return DateTime.SpecifyKind(dt - offset, DateTimeKind.Utc);

        // Camera EXIF timestamps rarely include timezone; approximate solar offset from longitude
        // rounded to nearest 15-degree solar timezone band when OffsetTimeOriginal is absent.
        var approxOffsetHours = Math.Round(longitude / 15.0);
        return DateTime.SpecifyKind(dt.AddHours(-approxOffsetHours), DateTimeKind.Utc);
    }

    private static bool TryParseOffset(string raw, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        raw = raw.Trim();
        if (raw.Length < 5) return false;
        var sign = raw[0] == '-' ? -1 : 1;
        var clean = raw.TrimStart('+', '-');
        if (TimeSpan.TryParseExact(clean, [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var parsed))
        {
            offset = sign < 0 ? -parsed : parsed;
            return true;
        }
        return false;
    }

    private static double ToJulianDate(DateTime utc)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (utc - epoch).TotalDays + 2440587.5;
    }

    private static double NormalizeDeg(double deg)
    {
        var r = deg % 360.0;
        return r < 0 ? r + 360.0 : r;
    }

    private static string ToCompass(double azimuth)
    {
        string[] dirs = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];
        var idx = (int)Math.Round(azimuth / 22.5) % 16;
        return dirs[idx];
    }
}
