using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PhotoLocationEditor.App.Models;

public sealed class PhotoItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _status = "Pending";
    private string? _cameraMake;
    private string? _cameraModel;
    private string? _lensModel;
    private string? _focalLength;
    private string? _aperture;
    private string? _shutterSpeed;
    private int? _iso;
    private int? _imageWidth;
    private int? _imageHeight;
    private string? _orientation;
    private string? _colorSpace;
    private string? _artist;
    private string? _copyright;
    private string? _userComment;
    private string? _dateTaken;
    private string? _offsetTime;
    private double? _latitude;
    private double? _longitude;
    private double? _altitude;
    private long _fileSizeBytes;
    private DateTime _fileModificationTime;
    private Services.ImageFormat _detectedFormat = Services.ImageFormat.Unknown;
    private SolarInfo? _solarInfo;

    public PhotoItem(string path)
    {
        Id = Guid.NewGuid().ToString("N")[..12];
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
    }

    public string Id { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string Path { get; private set; }
    public string FileName { get; private set; }
    public string Directory { get; private set; }

    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set => SetField(ref _fileSizeBytes, value);
    }

    public DateTime FileModificationTime
    {
        get => _fileModificationTime;
        set => SetField(ref _fileModificationTime, value);
    }

    public void UpdatePath(string newPath)
    {
        Path = newPath;
        FileName = System.IO.Path.GetFileName(newPath);
        Directory = System.IO.Path.GetDirectoryName(newPath) ?? string.Empty;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Directory));
        OnPropertyChanged(nameof(IsExtensionMismatched));
        OnPropertyChanged(nameof(MismatchDisplay));
        OnPropertyChanged(nameof(WrongExtensionHint));
    }

    public string? CameraMake
    {
        get => _cameraMake;
        set
        {
            if (SetField(ref _cameraMake, value))
                OnPropertyChanged(nameof(CameraDisplay));
        }
    }

    public string? CameraModel
    {
        get => _cameraModel;
        set
        {
            if (SetField(ref _cameraModel, value))
                OnPropertyChanged(nameof(CameraDisplay));
        }
    }

    public string? LensModel { get => _lensModel; set => SetField(ref _lensModel, value); }
    public string? FocalLength { get => _focalLength; set => SetField(ref _focalLength, value); }
    public string? Aperture { get => _aperture; set => SetField(ref _aperture, value); }
    public string? ShutterSpeed { get => _shutterSpeed; set => SetField(ref _shutterSpeed, value); }
    public int? Iso { get => _iso; set => SetField(ref _iso, value); }
    public int? ImageWidth { get => _imageWidth; set => SetField(ref _imageWidth, value); }
    public int? ImageHeight { get => _imageHeight; set => SetField(ref _imageHeight, value); }
    public string? Orientation { get => _orientation; set => SetField(ref _orientation, value); }
    public string? ColorSpace { get => _colorSpace; set => SetField(ref _colorSpace, value); }
    public string? Artist { get => _artist; set => SetField(ref _artist, value); }
    public string? Copyright { get => _copyright; set => SetField(ref _copyright, value); }
    public string? UserComment { get => _userComment; set => SetField(ref _userComment, value); }
    public string? OffsetTime { get => _offsetTime; set => SetField(ref _offsetTime, value); }
    public SolarInfo? SolarInfo { get => _solarInfo; set => SetField(ref _solarInfo, value); }

    public string CameraDisplay
    {
        get
        {
            var make = CameraMake?.Trim();
            var model = CameraModel?.Trim();
            if (string.IsNullOrWhiteSpace(make))
                return model ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model))
                return make;
            return model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                ? model
                : $"{make} {model}";
        }
    }

    private string? _dateTakenDate;
    private string? _dateTakenTime;

    public string? DateTaken
    {
        get => _dateTaken;
        set
        {
            if (SetField(ref _dateTaken, value))
            {
                ParseDateTime(value);
                OnPropertyChanged(nameof(DateTakenDate));
                OnPropertyChanged(nameof(DateTakenTime));
                RecomputeSolar();
            }
        }
    }

    public string? DateTakenDate => _dateTakenDate ?? (_dateTaken?.Length >= 10 ? _dateTaken[..10] : _dateTaken);
    public string? DateTakenTime => _dateTakenTime ?? (_dateTaken?.Length >= 16 ? _dateTaken[11..16] : null);

    private void ParseDateTime(string? value)
    {
        _dateTakenDate = null;
        _dateTakenTime = null;
        if (string.IsNullOrWhiteSpace(value)) return;
        var parts = value.Trim().Split(' ');
        if (parts.Length >= 1 && parts[0].Length >= 10)
            _dateTakenDate = parts[0][..10];
        if (parts.Length >= 2 && parts[1].Length >= 5)
            _dateTakenTime = parts[1][..5];
    }

    public double? Latitude
    {
        get => _latitude;
        set
        {
            if (SetField(ref _latitude, value))
                RefreshComputed();
        }
    }

    public double? Longitude
    {
        get => _longitude;
        set
        {
            if (SetField(ref _longitude, value))
                RefreshComputed();
        }
    }

    public double? Altitude
    {
        get => _altitude;
        set
        {
            if (SetField(ref _altitude, value))
                RefreshComputed();
        }
    }

    public string HasGps => Latitude.HasValue && Longitude.HasValue ? "GPS" : "-";

    public string GpsDisplay =>
        Latitude.HasValue && Longitude.HasValue
            ? Altitude.HasValue
                ? $"{Latitude:0.######}, {Longitude:0.######}, {Altitude:0.#} m"
                : $"{Latitude:0.######}, {Longitude:0.######}"
            : "-";

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public Services.ImageFormat DetectedFormat => _detectedFormat;
    public string FormatDisplay => Services.FormatDetector.GetFormatLabel(_detectedFormat);
    public bool IsVideo => Services.FormatDetector.IsVideoFormat(_detectedFormat);
    public bool IsMetadataWritable => Services.ExifToolService.IsSupportedMetadataWrite(Path);

    public bool IsExtensionMismatched =>
        _detectedFormat != Services.ImageFormat.Unknown &&
        !Services.FormatDetector.IsExtensionValid(Path, _detectedFormat);

    public string MismatchDisplay => IsExtensionMismatched ? "⚠" : string.Empty;

    public string WrongExtensionHint => IsExtensionMismatched
        ? $"实际: {FormatDisplay}, 后缀: {System.IO.Path.GetExtension(Path)}"
        : string.Empty;

    public DateTime FileCreationTime { get; private set; }
    public string FileCreationTimeDisplay => FileCreationTime == default ? "?" : FileCreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public void SetFileCreationTime(DateTime value)
    {
        if (FileCreationTime == value)
            return;

        FileCreationTime = value;
        OnPropertyChanged(nameof(FileCreationTime));
        OnPropertyChanged(nameof(FileCreationTimeDisplay));
    }

    public void SetDetectedFormat(Services.ImageFormat format)
    {
        if (SetField(ref _detectedFormat, format))
        {
            OnPropertyChanged(nameof(FormatDisplay));
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(IsExtensionMismatched));
            OnPropertyChanged(nameof(MismatchDisplay));
            OnPropertyChanged(nameof(WrongExtensionHint));
        }
    }

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(HasGps));
        OnPropertyChanged(nameof(GpsDisplay));
        RecomputeSolar();
    }

    public void RecomputeSolar()
    {
        if (Latitude is double lat && Longitude is double lon &&
            Services.DateIntelligenceService.TryParseExifDateTime(DateTaken, out var dt))
        {
            SolarInfo = Services.SolarCalculator.Compute(lat, lon, dt, OffsetTime);
        }
        else
        {
            SolarInfo = null;
        }
    }

    public MetadataSnapshot CreateSnapshot() => new(
        Path,
        Path,
        Latitude,
        Longitude,
        Altitude,
        DateTaken,
        CameraMake,
        CameraModel,
        LensModel,
        Artist,
        Copyright);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
