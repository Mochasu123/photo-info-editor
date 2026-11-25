namespace PhotoLocationEditor.App.Models;

public sealed record SolarInfo(
    double ElevationDegrees,
    double AzimuthDegrees,
    string Phase,
    string CompassDirection);

public enum DateIssueCategory
{
    Consistent,
    MissingExifWithFilenameTime,
    MissingExifWithFileTime,
    InvalidZeroExif,
    ExifLaterThanFile,
    ManualNeeded
}

public sealed record DateDiagnosisItem(
    string PhotoId,
    string Path,
    string FileName,
    string? ExifDate,
    string? FilenameDate,
    string? SuggestedDate,
    DateTime FileCreation,
    DateTime FileModification,
    DateIssueCategory Category,
    string ReasonCode,
    string Detail);

public sealed record MetadataSnapshot(
    string CurrentPath,
    string OriginalPath,
    double? Latitude,
    double? Longitude,
    double? Altitude,
    string? DateTaken,
    string? CameraMake,
    string? CameraModel,
    string? LensModel,
    string? Artist,
    string? Copyright);

public sealed record UndoOperation(
    string Id,
    string Description,
    DateTime TimestampUtc,
    IReadOnlyList<MetadataSnapshot> Snapshots);

public sealed record TrackPoint(
    double Latitude,
    double Longitude,
    double? Elevation,
    DateTime TimestampUtc);

public sealed record TrackMatchProposal(
    string PhotoId,
    string FileName,
    string? CaptureTime,
    double Latitude,
    double Longitude,
    double? Altitude,
    double TimeDeltaSeconds,
    bool Interpolated);

public sealed record ConversionOptions(
    Services.ImageFormat TargetFormat,
    WriteMode WriteMode,
    string? OutputDirectory,
    int JpegQuality = 92,
    int? MaxDimension = null);

public sealed record CameraInfoEdit(
    string? Make,
    string? Model,
    string? LensModel,
    string? Artist,
    string? Copyright,
    string? UserComment);

public enum PrivacyStripMode
{
    GpsOnly,
    PrivacyCleanKeepExif,
    PrivacyCleanFull
}
