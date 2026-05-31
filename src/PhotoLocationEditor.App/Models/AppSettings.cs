namespace PhotoLocationEditor.App.Models;

public sealed class AppSettings
{
    public string AMapJsKey { get; set; } = string.Empty;
    public string AMapSecurityJsCode { get; set; } = string.Empty;

    // UI persistence
    public int LastWriteMode { get; set; } = 2;
    public string LastOutputDirectory { get; set; } = string.Empty;
    public string LastLanguage { get; set; } = "zh";
    public string LastFilterText { get; set; } = string.Empty;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int WindowStateValue { get; set; }
    public string Theme { get; set; } = "sepia";
    public int ReferencePhotoTabIndex { get; set; }
    public List<int> ReferencePhotoTabOrder { get; set; } = [0, 1];
    public List<int> ColumnDisplayOrder { get; set; } = [];
}
