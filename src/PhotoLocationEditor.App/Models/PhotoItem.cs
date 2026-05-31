using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoLocationEditor.App.Models;

public sealed class PhotoItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _status = "Pending";
    private string? _cameraMake;
    private string? _cameraModel;
    private string? _dateTaken;
    private double? _latitude;
    private double? _longitude;
    private double? _altitude;
    private Services.ImageFormat _detectedFormat = Services.ImageFormat.Unknown;

    public PhotoItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string Path { get; private set; }
    public string FileName { get; private set; }
    public string Directory { get; private set; }

    public void UpdatePath(string newPath)
    {
        Path = newPath;
        FileName = System.IO.Path.GetFileName(newPath);
        Directory = System.IO.Path.GetDirectoryName(newPath) ?? string.Empty;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Directory));
    }

    public string? CameraMake
    {
        get => _cameraMake;
        set
        {
            if (SetField(ref _cameraMake, value))
            {
                OnPropertyChanged(nameof(CameraDisplay));
            }
        }
    }

    public string? CameraModel
    {
        get => _cameraModel;
        set
        {
            if (SetField(ref _cameraModel, value))
            {
                OnPropertyChanged(nameof(CameraDisplay));
            }
        }
    }

    public string CameraDisplay
    {
        get
        {
            var make = CameraMake?.Trim();
            var model = CameraModel?.Trim();
            if (string.IsNullOrWhiteSpace(make))
            {
                return model ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                return make;
            }

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
            {
                RefreshComputed();
            }
        }
    }

    public double? Longitude
    {
        get => _longitude;
        set
        {
            if (SetField(ref _longitude, value))
            {
                RefreshComputed();
            }
        }
    }

    public double? Altitude
    {
        get => _altitude;
        set
        {
            if (SetField(ref _altitude, value))
            {
                RefreshComputed();
            }
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
    public bool IsExtensionMismatched =>
        _detectedFormat != Services.ImageFormat.Unknown &&
        !Path.EndsWith(Services.FormatDetector.GetStandardExtension(_detectedFormat), StringComparison.OrdinalIgnoreCase);
    public string MismatchDisplay => IsExtensionMismatched ? "⚠" : string.Empty;
    public string WrongExtensionHint => IsExtensionMismatched
        ? $"实际: {FormatDisplay}, 后缀: {System.IO.Path.GetExtension(Path)}"
        : string.Empty;

    public DateTime FileCreationTime { get; set; }
    public string FileCreationTimeDisplay => FileCreationTime == default ? "?" : FileCreationTime.ToString("yyyy-MM-dd HH:mm:ss");

    public void SetDetectedFormat(Services.ImageFormat format)
    {
        if (SetField(ref _detectedFormat, format))
        {
            OnPropertyChanged(nameof(FormatDisplay));
            OnPropertyChanged(nameof(IsExtensionMismatched));
            OnPropertyChanged(nameof(MismatchDisplay));
            OnPropertyChanged(nameof(WrongExtensionHint));
        }
    }

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(HasGps));
        OnPropertyChanged(nameof(GpsDisplay));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
