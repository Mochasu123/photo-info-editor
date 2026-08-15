using System.Globalization;
using System.Text.RegularExpressions;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public static partial class GpsParser
{
    public static bool TryParse(string input, out GpsCoordinate coordinate, out string error)
    {
        coordinate = new GpsCoordinate(0, 0);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter a coordinate first.";
            return false;
        }

        var normalized = Normalize(input);
        var altitude = TryParseAltitude(normalized);

        if (TryParseDms(normalized, altitude, out coordinate) ||
            TryParseDirectionalDecimal(normalized, altitude, out coordinate) ||
            TryParsePlainDecimal(normalized, altitude, out coordinate))
        {
            if (Math.Abs(coordinate.Latitude) > 90)
            {
                error = "Latitude must be between -90 and 90.";
                return false;
            }

            if (Math.Abs(coordinate.Longitude) > 180)
            {
                error = "Longitude must be between -180 and 180.";
                return false;
            }

            return true;
        }

        error = "Supported examples: 44.604114, 81.370087 or 44°36'14.81\"N, 81°22'12.31\"E.";
        return false;
    }

    private static bool TryParsePlainDecimal(string input, double? altitude, out GpsCoordinate coordinate)
    {
        coordinate = new GpsCoordinate(0, 0);
        var matches = NumberRegex().Matches(input);
        if (matches.Count < 2)
        {
            return false;
        }

        var lat = double.Parse(matches[0].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(matches[1].Value, CultureInfo.InvariantCulture);
        coordinate = new GpsCoordinate(lat, lon, altitude);
        return true;
    }

    private static bool TryParseDirectionalDecimal(string input, double? altitude, out GpsCoordinate coordinate)
    {
        coordinate = new GpsCoordinate(0, 0);
        var matches = DirectionalDecimalRegex().Matches(input);
        if (matches.Count < 2)
        {
            return false;
        }

        double? lat = null;
        double? lon = null;
        foreach (Match match in matches)
        {
            var prefix = match.Groups["prefix"].Value;
            var suffix = match.Groups["suffix"].Value;
            var direction = string.IsNullOrWhiteSpace(prefix) ? suffix : prefix;
            var raw = match.Groups["pv"].Success ? match.Groups["pv"].Value : match.Groups["sv"].Value;
            var value = double.Parse(raw, CultureInfo.InvariantCulture);
            value = ApplyDirection(value, direction);

            if (direction.Equals("N", StringComparison.OrdinalIgnoreCase) ||
                direction.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                lat = value;
            }
            else
            {
                lon = value;
            }
        }

        if (!lat.HasValue || !lon.HasValue)
        {
            return false;
        }

        coordinate = new GpsCoordinate(lat.Value, lon.Value, altitude);
        return true;
    }

    private static bool TryParseDms(string input, double? altitude, out GpsCoordinate coordinate)
    {
        coordinate = new GpsCoordinate(0, 0);
        var matches = DmsRegex().Matches(input);
        if (matches.Count < 2)
        {
            return false;
        }

        double? lat = null;
        double? lon = null;
        foreach (Match match in matches)
        {
            var degrees = double.Parse(match.Groups["deg"].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups["min"].Success ? double.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
            var seconds = match.Groups["sec"].Success ? double.Parse(match.Groups["sec"].Value, CultureInfo.InvariantCulture) : 0;
            var direction = match.Groups["dir"].Value;
            var value = degrees + minutes / 60 + seconds / 3600;
            value = ApplyDirection(value, direction);

            if (direction.Equals("N", StringComparison.OrdinalIgnoreCase) ||
                direction.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                lat = value;
            }
            else
            {
                lon = value;
            }
        }

        if (!lat.HasValue || !lon.HasValue)
        {
            return false;
        }

        coordinate = new GpsCoordinate(lat.Value, lon.Value, altitude);
        return true;
    }

    private static double ApplyDirection(double value, string direction)
    {
        return direction.Equals("S", StringComparison.OrdinalIgnoreCase) ||
               direction.Equals("W", StringComparison.OrdinalIgnoreCase)
            ? -Math.Abs(value)
            : Math.Abs(value);
    }

    private static double? TryParseAltitude(string input)
    {
        var match = AltitudeRegex().Match(input);
        return match.Success
            ? double.Parse(match.Groups["alt"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private static string Normalize(string input)
    {
        return input
            .Replace('，', ',')
            .Replace('：', ':')
            .Replace('′', '\'')
            .Replace('’', '\'')
            .Replace('″', '"')
            .Replace('“', '"')
            .Replace('”', '"');
    }

    // A direction letter must be attached to its value (prefix `N33.5` or suffix `33.5S`),
    // so a letter before the NEXT value (`S33.5 W70.5`) is never consumed as a suffix.
    [GeneratedRegex(@"(?:(?<prefix>[NSEW])\s*(?<pv>[-+]?\d+(?:\.\d+)?))|(?:(?<sv>[-+]?\d+(?:\.\d+)?)\s*(?<suffix>[NSEW]))", RegexOptions.IgnoreCase)]
    private static partial Regex DirectionalDecimalRegex();

    [GeneratedRegex(@"(?<deg>\d+(?:\.\d+)?)\D+(?<min>\d+(?:\.\d+)?)?\D*(?<sec>\d+(?:\.\d+)?)?\D*(?<dir>[NSEW])", RegexOptions.IgnoreCase)]
    private static partial Regex DmsRegex();

    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(?:alt|altitude|海拔)\s*[:=]?\s*(?<alt>[-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex AltitudeRegex();
}
