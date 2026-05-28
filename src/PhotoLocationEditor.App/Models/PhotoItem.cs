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

    public string Path { get; }
    public string FileName { get; }
    public string Directory { get; }

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

    public string? DateTaken
    {
        get => _dateTaken;
        set => SetField(ref _dateTaken, value);
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
