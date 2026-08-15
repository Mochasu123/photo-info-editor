using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;

namespace PhotoLocationEditor.App.Tests;

[TestClass]
public sealed class GpsParserTests
{
    [DataTestMethod]
    [DataRow("39.9042, 116.4074")]
    [DataRow("44.604114, 81.370087")]
    [DataRow("N44.604114 E81.370087")]
    [DataRow("44.604114N 81.370087E")]
    [DataRow("S33.5 W70.5")]
    [DataRow("44°36'14.81\"N, 81°22'12.31\"E")]
    [DataRow("44.604114, 81.370087, alt=123.4")]
    public void TryParse_accepts_supported_formats(string input)
    {
        var ok = GpsParser.TryParse(input, out var coordinate, out var error);
        Assert.IsTrue(ok, error);
        Assert.IsTrue(coordinate.Latitude is >= -90 and <= 90);
        Assert.IsTrue(coordinate.Longitude is >= -180 and <= 180);
    }

    [TestMethod]
    public void TryParse_rejects_latitude_out_of_range()
    {
        Assert.IsFalse(GpsParser.TryParse("91, 0", out _, out _));
        Assert.IsFalse(GpsParser.TryParse("-90.0001, 0", out _, out _));
    }

    [TestMethod]
    public void TryParse_applies_south_and_west_directions()
    {
        Assert.IsTrue(GpsParser.TryParse("S33.5 W70.5", out var coordinate, out _));
        Assert.AreEqual(-33.5, coordinate.Latitude, 1e-9);
        Assert.AreEqual(-70.5, coordinate.Longitude, 1e-9);
    }

    [TestMethod]
    public void TryParse_parses_altitude()
    {
        Assert.IsTrue(GpsParser.TryParse("30.5, 120.1, 海拔=88.5", out var coordinate, out _));
        Assert.AreEqual(88.5, coordinate.Altitude!.Value, 1e-9);
    }
}

[TestClass]
public sealed class CoordinateTransformTests
{
    [TestMethod]
    public void Wgs84_to_gcj02_and_back_is_stable_inside_china()
    {
        var original = new GpsCoordinate(39.9042, 116.4074);
        var gcj = CoordinateTransform.Wgs84ToGcj02(original);
        var back = CoordinateTransform.Gcj02ToWgs84(gcj);

        Assert.AreEqual(original.Latitude, back.Latitude, 1e-3);
        Assert.AreEqual(original.Longitude, back.Longitude, 1e-3);
    }

    [TestMethod]
    public void Transform_is_identity_outside_china()
    {
        var original = new GpsCoordinate(40.7128, -74.0060);
        var converted = CoordinateTransform.Wgs84ToGcj02(original);

        Assert.AreEqual(original.Latitude, converted.Latitude, 1e-12);
        Assert.AreEqual(original.Longitude, converted.Longitude, 1e-12);
    }

    [TestMethod]
    public void Bd09_to_wgs84_does_not_produce_nan()
    {
        var bd = new GpsCoordinate(39.915, 116.404);
        var wgs = CoordinateTransform.ToWgs84(bd, CoordinateSystemKind.Bd09);

        Assert.IsFalse(double.IsNaN(wgs.Latitude));
        Assert.IsFalse(double.IsNaN(wgs.Longitude));
    }
}

[TestClass]
public sealed class FormatDetectorTests
{
    [TestMethod]
    public void Detect_recognizes_magic_bytes()
    {
        AssertDetected(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }, ImageFormat.Jpeg, ".jpg");
        AssertDetected(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ImageFormat.Png, ".png");
        AssertDetected(new byte[] { 0x42, 0x4D, 0x00, 0x00, 0x00, 0x00 }, ImageFormat.Bmp, ".bmp");
        AssertDetected(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x00 }, ImageFormat.Mkv, ".mkv");
    }

    private static void AssertDetected(byte[] bytes, ImageFormat expected, string tempExtension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"format-test-{Guid.NewGuid():N}{tempExtension}");
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.AreEqual(expected, FormatDetector.Detect(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
