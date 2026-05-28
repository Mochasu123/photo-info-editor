using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App.Services;

public static class CoordinateTransform
{
    private const double A = 6378245.0;
    private const double Ee = 0.00669342162296594323;
    private const double XPi = Math.PI * 3000.0 / 180.0;

    public static GpsCoordinate ToWgs84(GpsCoordinate coordinate, CoordinateSystemKind sourceSystem)
    {
        return sourceSystem switch
        {
            CoordinateSystemKind.Gcj02 => Gcj02ToWgs84(coordinate),
            CoordinateSystemKind.Bd09 => Gcj02ToWgs84(Bd09ToGcj02(coordinate)),
            _ => coordinate
        };
    }

    public static GpsCoordinate Wgs84ToGcj02(GpsCoordinate coordinate)
    {
        if (IsOutsideChina(coordinate.Latitude, coordinate.Longitude))
        {
            return coordinate;
        }

        var delta = TransformDelta(coordinate.Latitude, coordinate.Longitude);
        return new GpsCoordinate(
            coordinate.Latitude + delta.Latitude,
            coordinate.Longitude + delta.Longitude,
            coordinate.Altitude);
    }

    public static GpsCoordinate Gcj02ToWgs84(GpsCoordinate coordinate)
    {
        if (IsOutsideChina(coordinate.Latitude, coordinate.Longitude))
        {
            return coordinate;
        }

        var delta = TransformDelta(coordinate.Latitude, coordinate.Longitude);
        return new GpsCoordinate(
            coordinate.Latitude - delta.Latitude,
            coordinate.Longitude - delta.Longitude,
            coordinate.Altitude);
    }

    public static GpsCoordinate Bd09ToGcj02(GpsCoordinate coordinate)
    {
        var x = coordinate.Longitude - 0.0065;
        var y = coordinate.Latitude - 0.006;
        var z = Math.Sqrt(x * x + y * y) - 0.00002 * Math.Sin(y * XPi);
        var theta = Math.Atan2(y, x) - 0.000003 * Math.Cos(x * XPi);
        return new GpsCoordinate(
            z * Math.Sin(theta),
            z * Math.Cos(theta),
            coordinate.Altitude);
    }

    private static (double Latitude, double Longitude) TransformDelta(double latitude, double longitude)
    {
        var dLat = TransformLat(longitude - 105.0, latitude - 35.0);
        var dLon = TransformLon(longitude - 105.0, latitude - 35.0);
        var radLat = latitude / 180.0 * Math.PI;
        var magic = Math.Sin(radLat);
        magic = 1 - Ee * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        dLat = (dLat * 180.0) / ((A * (1 - Ee)) / (magic * sqrtMagic) * Math.PI);
        dLon = (dLon * 180.0) / (A / sqrtMagic * Math.Cos(radLat) * Math.PI);
        return (dLat, dLon);
    }

    private static bool IsOutsideChina(double latitude, double longitude)
    {
        return longitude < 72.004 || longitude > 137.8347 || latitude < 0.8293 || latitude > 55.8271;
    }

    private static double TransformLat(double x, double y)
    {
        var ret = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(y * Math.PI) + 40.0 * Math.Sin(y / 3.0 * Math.PI)) * 2.0 / 3.0;
        ret += (160.0 * Math.Sin(y / 12.0 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30.0)) * 2.0 / 3.0;
        return ret;
    }

    private static double TransformLon(double x, double y)
    {
        var ret = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        ret += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        ret += (20.0 * Math.Sin(x * Math.PI) + 40.0 * Math.Sin(x / 3.0 * Math.PI)) * 2.0 / 3.0;
        ret += (150.0 * Math.Sin(x / 12.0 * Math.PI) + 300.0 * Math.Sin(x / 30.0 * Math.PI)) * 2.0 / 3.0;
        return ret;
    }
}
