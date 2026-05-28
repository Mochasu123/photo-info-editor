namespace PhotoLocationEditor.App.Models;

public sealed record GpsCoordinate(
    double Latitude,
    double Longitude,
    double? Altitude = null)
{
    public string LatitudeRef => Latitude >= 0 ? "N" : "S";
    public string LongitudeRef => Longitude >= 0 ? "E" : "W";

    public string Display =>
        Altitude is null
            ? $"{Latitude:0.######}, {Longitude:0.######}"
            : $"{Latitude:0.######}, {Longitude:0.######}, {Altitude:0.#} m";
}
