namespace PhotoLocationEditor.App.Models;

public sealed class SavedLocationPin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Altitude { get; set; }
    public long CreatedAtUnix { get; set; }
}

public sealed class AppSettings
{
    public string AMapJsKey { get; set; } = string.Empty;
    public string AMapSecurityJsCode { get; set; } = string.Empty;

    // UI persistence (backwards-compatible with v0.3.0 settings.json)
    public int LastWriteMode { get; set; } = 2;
    public string LastOutputDirectory { get; set; } = string.Empty;
    public string LastLanguage { get; set; } = "zh";
    public string LastFilterText { get; set; } = string.Empty;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int WindowStateValue { get; set; }
    public string Theme { get; set; } = "obsidian";
    public int ReferencePhotoTabIndex { get; set; }
    public List<int> ReferencePhotoTabOrder { get; set; } = [0, 1];
    public List<int> ColumnDisplayOrder { get; set; } = [];

    // Studio v1.0 preferences
    public bool PureDarkroomMode { get; set; }
    public string ViewMode { get; set; } = "grid";
    public string MapProvider { get; set; } = "osm";
    public bool SmartSkipConfirm { get; set; }
    public bool SyncFileTimestamps { get; set; }
    public int JpegQuality { get; set; } = 92;
    public int ThumbSize { get; set; } = 230;
    public List<SavedLocationPin> SavedLocations { get; set; } = [];
    public List<SavedLocationPin> RecentLocations { get; set; } = [];
}
