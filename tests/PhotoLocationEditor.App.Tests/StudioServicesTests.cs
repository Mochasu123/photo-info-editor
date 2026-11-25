using System.Drawing;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;

namespace PhotoLocationEditor.App.Tests;

[TestClass]
public sealed class DateIntelligenceTests
{
    [TestMethod]
    public void TryExtractFilenameTimestamp_parses_camera_and_wechat_epoch_names()
    {
        Assert.IsTrue(DateIntelligenceService.TryExtractFilenameTimestamp("IMG_20250816_183012.jpg", out var dt1));
        Assert.AreEqual(new DateTime(2025, 8, 16, 18, 30, 12), dt1);

        Assert.IsTrue(DateIntelligenceService.TryExtractFilenameTimestamp("Screenshot_2025-04-04-06-28-44.png", out var dt2));
        Assert.AreEqual(new DateTime(2025, 4, 4, 6, 28, 44), dt2);

        // 1728468120000 ms -> valid 2024 timestamp
        Assert.IsTrue(DateIntelligenceService.TryExtractFilenameTimestamp("mmexport1728468120000.jpg", out var dt3));
        Assert.AreEqual(2024, dt3.Year);

        Assert.IsFalse(DateIntelligenceService.TryExtractFilenameTimestamp("IMG_20259999_999999.jpg", out _));
    }

    [TestMethod]
    public void ShiftExifTime_and_GenerateSequence_compute_accurate_timestamps()
    {
        var shifted = DateIntelligenceService.ShiftExifTime("2025:10:18 22:30:00", TimeSpan.FromHours(3));
        Assert.AreEqual("2025:10:19 01:30:00", shifted);

        var p1 = new PhotoItem("a.jpg");
        var p2 = new PhotoItem("b.jpg");
        var seq = DateIntelligenceService.GenerateSequence([p1, p2], new DateTime(2025, 10, 18, 10, 0, 0), 5);
        Assert.AreEqual("2025:10:18 10:00:00", seq[0].Date);
        Assert.AreEqual("2025:10:18 10:00:05", seq[1].Date);
    }

    [TestMethod]
    public void Diagnose_identifies_missing_exif_with_filename_recovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pie-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var pPath = Path.Combine(dir, "IMG_20250816_142030.jpg");
            File.WriteAllBytes(pPath, [0xFF, 0xD8, 0xFF, 0xE0]);
            var item = new PhotoItem(pPath);
            item.SetFileCreationTime(new DateTime(2026, 1, 1, 12, 0, 0));

            var diag = DateIntelligenceService.Diagnose([item]);
            Assert.AreEqual(1, diag.Count);
            Assert.AreEqual(DateIssueCategory.MissingExifWithFilenameTime, diag[0].Category);
            Assert.AreEqual("2025:08:16 14:20:30", diag[0].SuggestedDate);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

[TestClass]
public sealed class SolarCalculatorTests
{
    [TestMethod]
    public void Compute_returns_daylight_for_summer_noon_and_night_for_midnight()
    {
        var noon = SolarCalculator.Compute(39.9042, 116.4074, new DateTime(2025, 6, 21, 12, 15, 0), "+08:00");
        Assert.AreEqual("Daylight", noon.Phase);
        Assert.IsTrue(noon.ElevationDegrees > 60.0);

        var midnight = SolarCalculator.Compute(39.9042, 116.4074, new DateTime(2025, 6, 21, 0, 0, 0), "+08:00");
        Assert.AreEqual("Night", midnight.Phase);
        Assert.IsTrue(midnight.ElevationDegrees < -15.0);
    }
}

[TestClass]
public sealed class GeoTrackServiceTests
{
    [TestMethod]
    public void ParseGpx_interpolates_photo_coordinates_and_exports_formats()
    {
        const string gpx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><trkseg>
                <trkpt lat="35.0000" lon="135.0000"><ele>100</ele><time>2025-10-18T08:00:00Z</time></trkpt>
                <trkpt lat="35.0100" lon="135.0200"><ele>120</ele><time>2025-10-18T08:10:00Z</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;

        var points = GeoTrackService.ParseGpx(gpx, isFilePath: false);
        Assert.AreEqual(2, points.Count);

        var photo = new PhotoItem("DSC0001.JPG")
        {
            DateTaken = "2025:10:18 16:05:00" // UTC+8 = 08:05:00Z (exact midpoint)
        };

        var matches = GeoTrackService.MatchPhotosToTrack([photo], points, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8));
        Assert.AreEqual(1, matches.Count);
        Assert.IsTrue(matches[0].Interpolated);
        Assert.AreEqual(35.0050, matches[0].Latitude, 1e-4);
        Assert.AreEqual(135.0100, matches[0].Longitude, 1e-4);
        Assert.AreEqual(110.0, matches[0].Altitude!.Value, 0.5);

        photo.Latitude = matches[0].Latitude;
        photo.Longitude = matches[0].Longitude;
        Assert.IsTrue(GeoTrackService.ExportGpx([photo]).Contains("<trkpt lat=\"35.005\""));
        Assert.IsTrue(GeoTrackService.ExportGeoJson([photo]).Contains("\"FeatureCollection\""));
        Assert.IsTrue(GeoTrackService.ExportCsv([photo]).Contains("DSC0001.JPG"));
    }
}

[TestClass]
public sealed class ExtendedFormatDetectorTests
{
    [TestMethod]
    public void Detect_recognizes_avif_and_raw_signatures()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pie-fmt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var avif = Path.Combine(dir, "sample.avif");
            File.WriteAllBytes(avif, [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f']);
            Assert.AreEqual(ImageFormat.Avif, FormatDetector.Detect(avif));

            var raf = Path.Combine(dir, "sample.raf");
            File.WriteAllBytes(raf, System.Text.Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW 0201"));
            Assert.AreEqual(ImageFormat.Raf, FormatDetector.Detect(raf));
            Assert.IsTrue(FormatDetector.IsRawFormat(ImageFormat.Raf));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

[TestClass]
public sealed class ExifToolDaemonIntegrationTests
{
    [TestMethod]
    public async Task Daemon_reads_writes_and_undoes_utf8_jpeg_metadata()
    {
        var exifToolPath = ExifToolService.DefaultPath;
        if (!File.Exists(exifToolPath))
            Assert.Inconclusive("Bundled exiftool.exe not present in test output.");

        var dir = Path.Combine(Path.GetTempPath(), $"pie-et-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var photoPath = Path.Combine(dir, "测试照片_🌸.jpg");
            using (var bmp = new Bitmap(64, 48))
            {
                bmp.Save(photoPath, System.Drawing.Imaging.ImageFormat.Jpeg);
            }

            await using var service = new ExifToolService(exifToolPath);
            await service.WarmUpAsync();
            Assert.IsTrue(service.IsDaemonRunning, "ExifTool stay_open daemon should be running.");

            var photo = new PhotoItem(photoPath);
            await service.ReadMetadataAsync([photo]);

            // Snapshot pre-edit state for Undo
            var undo = new MetadataUndoService();
            undo.Push("Write GPS & Date", [photo.CreateSnapshot()]);

            // Write GPS + Date + Camera
            var coord = new GpsCoordinate(39.9042, 116.4074, 52.5);
            await service.WriteGpsAsync([photo], coord, WriteMode.DirectInPlace, null);
            await service.WriteDateAsync([photo], "2025:10:18 17:42:15", WriteMode.DirectInPlace, null, syncFileTimestamps: true);
            await service.WriteCameraInfoAsync([photo], new CameraInfoEdit("FUJIFILM", "X-T5", "XF23mmF1.4", "Tester", null, null), WriteMode.DirectInPlace, null);

            // Re-read from disk with a fresh PhotoItem to verify persistence
            var check = new PhotoItem(photoPath);
            await service.ReadMetadataAsync([check]);
            Assert.AreEqual(39.9042, check.Latitude!.Value, 1e-4);
            Assert.AreEqual(116.4074, check.Longitude!.Value, 1e-4);
            Assert.AreEqual("2025:10:18 17:42:15", check.DateTaken);
            Assert.AreEqual("FUJIFILM", check.CameraMake);
            Assert.AreEqual("X-T5", check.CameraModel);
            Assert.AreEqual(new DateTime(2025, 10, 18, 17, 42, 15), File.GetLastWriteTime(photoPath));

            // Undo and re-read from disk
            var op = undo.Pop()!;
            await service.RestoreSnapshotsAsync(op.Snapshots, [photo]);
            var afterUndo = new PhotoItem(photoPath);
            await service.ReadMetadataAsync([afterUndo]);
            Assert.IsNull(afterUndo.Latitude);
            Assert.IsNull(afterUndo.DateTaken);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
