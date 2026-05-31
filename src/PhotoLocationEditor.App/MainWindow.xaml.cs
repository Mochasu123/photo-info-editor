using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;
using WinForms = System.Windows.Forms;

namespace PhotoLocationEditor.App;

public partial class MainWindow : Window
{
    private readonly ExifToolService _exifToolService = new(ExifToolService.DefaultPath);
    private readonly ImageConversionService _imageConversionService;
    private readonly AppSettingsService _settingsService = new();
    private AppSettings _settings;
    private GpsCoordinate? _currentCoordinate;
    private AppLanguage _language = AppLanguage.Chinese;
    private bool _isGpsPlaceholderVisible;
    private CoordinateSystemKind _inputCoordinateSystem = CoordinateSystemKind.Wgs84;
    private CancellationTokenSource? _toastCts;

    private enum FilterMode { All, NoGps, HasGps, Fuji, Huawei, ExtensionMismatch }
    private FilterMode _filterMode = FilterMode.All;

    public MainWindow()
    {
        Photos = new ObservableCollection<PhotoItem>();
        Photos.CollectionChanged += (_, _) => UpdateStats();
        _imageConversionService = new ImageConversionService(_exifToolService);
        _settings = _settingsService.Load();
        InitializeComponent();
        DataContext = this;

        // Theme
        for (var i = 0; i < App.ThemeNames.Length; i++)
            ThemeComboBox.Items.Add(new ComboBoxItem { Content = App.ThemeLabels[i], Tag = App.ThemeNames[i] });
        var savedTheme = _settings.Theme;
        ThemeComboBox.SelectedIndex = savedTheme == "dark" ? 2 : savedTheme == "sepia" ? 1 : 0;
        if (ThemeComboBox.SelectedIndex > 0) App.SetTheme(_settings.Theme);

        RestoreWindowState();
        LanguageComboBox.SelectedIndex = _settings.LastLanguage == "en" ? 1 : 0;
        WriteModeComboBox.SelectedIndex = _settings.LastWriteMode;
        if (!string.IsNullOrEmpty(_settings.LastOutputDirectory))
            OutputDirectoryTextBox.Text = _settings.LastOutputDirectory;
        if (_settings.ColumnDisplayOrder.Count == PhotoGrid.Columns.Count)
            for (var i = 0; i < PhotoGrid.Columns.Count; i++)
                PhotoGrid.Columns[i].DisplayIndex = _settings.ColumnDisplayOrder[i];
        ShowGpsPlaceholder();
        UpdateSelectedPhotoPanel();
        UpdateStats();
        FilterSearchBox.Text = _settings.LastFilterText ?? "";
        FilterSearchBox.TextChanged += FilterSearchBox_TextChanged;
        WriteModeComboBox_SelectionChanged(this, null);
        ConvertWriteModeComboBox_SelectionChanged(this, null);
    }

    public ObservableCollection<PhotoItem> Photos { get; }

    // ---- Persistence ----
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is ComboBoxItem themeItem)
            _settings.Theme = themeItem.Tag?.ToString() ?? "light";
        _settings.LastWriteMode = WriteModeComboBox.SelectedIndex;
        _settings.LastOutputDirectory = OutputDirectoryTextBox.Text;
        _settings.LastLanguage = _language == AppLanguage.English ? "en" : "zh";
        _settings.LastFilterText = FilterSearchBox.Text;
        _settings.WindowLeft = Left; _settings.WindowTop = Top;
        _settings.WindowWidth = Width; _settings.WindowHeight = Height;
        _settings.WindowStateValue = (int)WindowState;
        _settingsService.Save(_settings);
    }

    private void RestoreWindowState()
    {
        if (_settings.WindowLeft.HasValue) Left = _settings.WindowLeft.Value;
        if (_settings.WindowTop.HasValue) Top = _settings.WindowTop.Value;
        if (_settings.WindowWidth.HasValue) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight.HasValue) Height = _settings.WindowHeight.Value;
        if (_settings.WindowStateValue == (int)WindowState.Maximized) WindowState = WindowState.Maximized;
    }

    // ---- Keyboard global ----
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.A: SelectAll_Click(sender, e); e.Handled = true; return;
                case Key.I: InvertSelection_Click(sender, e); e.Handled = true; return;
                case Key.D0: SelectNone_Click(sender, e); e.Handled = true; return;
                case Key.W: WriteButton_Click(sender, e); e.Handled = true; return;
                case Key.OemPlus: case Key.Add: MarkHighlighted_Click(sender, e); e.Handled = true; return;
                case Key.OemMinus: case Key.Subtract: UnmarkHighlighted_Click(sender, e); e.Handled = true; return;
            }
        }
        if (e.Key == Key.Delete) { RemoveSelected_Click(sender, e); e.Handled = true; }
    }

    // ---- Stats ----
    private void UpdateMismatchStats()
    {
        if (MismatchStatsText is null) return;
        var mismatched = Photos.Where(p => p.IsExtensionMismatched).ToArray();
        if (mismatched.Length == 0)
        {
            MismatchStatsText.Text = "当前没有异常后缀 ✓";
            MismatchStatsText.Foreground = System.Windows.Media.Brushes.Green;
            FixExtensionsButton.IsEnabled = false;
            return;
        }
        var total = Photos.Count;
        var pct = total > 0 ? mismatched.Length * 100.0 / total : 0.0;
        var byType = mismatched.GroupBy(p => FormatDetector.GetFormatLabel(p.DetectedFormat));
        var typeStr = string.Join(", ", byType.Select(g => $"{g.Key}:{g.Count()}"));
        MismatchStatsText.Text = $"异常后缀: {mismatched.Length} 张 ({pct:F1}%) — {typeStr}";
        MismatchStatsText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        FixExtensionsButton.IsEnabled = true;
    }

    private void UpdateStats()
    {
        if (StatsTotalText is null) return;
        var total = Photos.Count;
        var gps = Photos.Count(p => p.Latitude.HasValue && p.Longitude.HasValue);
        var selected = Photos.Count(p => p.IsSelected);
        StatsTotalText.Text = string.Format(CultureInfo.CurrentCulture, T("StatsTotal"), total);
        StatsGpsText.Text = string.Format(CultureInfo.CurrentCulture, T("StatsHasGps"), gps);
        StatsSelectedText.Text = string.Format(CultureInfo.CurrentCulture, T("StatsSelected"), selected);
        UpdateMismatchStats();
    }

    // ---- Filter ----
    private void SetFilter(FilterMode mode) { _filterMode = mode; ApplyFilter(); }

    private void FilterSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(Photos);
        view.Filter = p => p is PhotoItem photo && MatchesFilterMode(photo) && MatchesSearch(photo);
        view.Refresh();
    }

    private bool MatchesFilterMode(PhotoItem photo) => _filterMode switch
    {
        FilterMode.NoGps => !photo.Latitude.HasValue || !photo.Longitude.HasValue,
        FilterMode.HasGps => photo.Latitude.HasValue && photo.Longitude.HasValue,
        FilterMode.Fuji => ExifToolService.IsFujifilm(photo),
        FilterMode.Huawei => (photo.CameraModel?.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase) == true) ||
                       (photo.CameraMake?.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase) == true),
        FilterMode.ExtensionMismatch => photo.IsExtensionMismatched,
        _ => true
    };

    private bool MatchesSearch(PhotoItem photo)
    {
        var search = FilterSearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        return photo.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (photo.CameraModel?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (photo.DateTaken?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) => SetFilter(FilterMode.All);
    private void FilterNoGps_Click(object sender, RoutedEventArgs e) => SetFilter(FilterMode.NoGps);
    private void FilterHasGps_Click(object sender, RoutedEventArgs e) => SetFilter(FilterMode.HasGps);
    private void FilterFuji_Click(object sender, RoutedEventArgs e) => SetFilter(FilterMode.Fuji);
    private void FilterHuawei_Click(object sender, RoutedEventArgs e) => SetFilter(FilterMode.Huawei);
    private void FilterMismatch_Click(object sender, RoutedEventArgs e) => SetFilter(FilterMode.ExtensionMismatch);

    // ---- Import ----
    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Image files|*.jpg;*.jpeg;*.heic;*.heif;*.hif;*.png;*.webp|All files|*.*" };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = T("FolderDialogTitle"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        var option = IncludeSubfoldersCheckBox.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var allFiles = Directory.EnumerateFiles(dlg.SelectedPath, "*", option).ToArray();
        var supported = allFiles.Where(ExifToolService.IsSupportedImage).ToArray();
        var unsupported = allFiles.Where(f => !ExifToolService.IsSupportedImage(f))
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
            .Select(g => $"{g.Key}:{g.Count()}").ToArray();
        if (supported.Length > 0 && unsupported.Length > 0)
            System.Windows.MessageBox.Show(this,
                $"已导入 {supported.Length} 张照片，有 {allFiles.Length - supported.Length} 张格式不支持（{string.Join(", ", unsupported)}），已跳过。",
                "导入报告", MessageBoxButton.OK, MessageBoxImage.Information);
        if (supported.Length > 0)
            await AddPathsAsync(supported);
        else if (unsupported.Length > 0)
            System.Windows.MessageBox.Show(this,
                $"文件夹中 {allFiles.Length} 张均不支持（{string.Join(", ", unsupported)}）。",
                "导入报告", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            StatusText.Text = T("NoNewPhotos");
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var dropped = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        await AddPathsAsync(ExpandDroppedPaths(dropped).ToArray());
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private static IEnumerable<string> ExpandDroppedPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (ExifToolService.IsSupportedImage(path) && File.Exists(path))
                yield return path;
            else if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Where(ExifToolService.IsSupportedImage))
                    yield return file;
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var existing = Photos.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = paths.Where(File.Exists).Where(ExifToolService.IsSupportedImage).Where(path => existing.Add(path)).Select(path => new PhotoItem(path)).ToArray();
        foreach (var item in added) Photos.Add(item);
        if (added.Length == 0) { StatusText.Text = T("NoNewPhotos"); return; }
        StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ReadingMetadata"), added.Length);
        try
        {
            await _exifToolService.ReadMetadataAsync(added);
            foreach (var photo in added)
            {
                photo.SetDetectedFormat(FormatDetector.Detect(photo.Path));
                try { photo.FileCreationTime = File.GetCreationTime(photo.Path); } catch { }
            }
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ImportedPhotos"), added.Length);
            UpdateSelectedPhotoPanel();
            UpdateStats();
        }
        catch (Exception ex)
        {
            foreach (var item in added) item.Status = "Read failed";
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, T("ReadFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- Selection ----
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var p in Photos) p.IsSelected = true; UpdateStats(); }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var p in Photos) p.IsSelected = false; UpdateStats(); }
    private void MarkHighlighted_Click(object sender, RoutedEventArgs e) { foreach (var p in GetHighlightedPhotos()) p.IsSelected = true; UpdateStats(); }
    private void UnmarkHighlighted_Click(object sender, RoutedEventArgs e) { foreach (var p in GetHighlightedPhotos()) p.IsSelected = false; UpdateStats(); }
    private void InvertSelection_Click(object sender, RoutedEventArgs e) { foreach (var p in Photos) p.IsSelected = !p.IsSelected; UpdateStats(); }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e) { for (var i = Photos.Count - 1; i >= 0; i--) if (Photos[i].IsSelected) Photos.RemoveAt(i); UpdateSelectedPhotoPanel(); UpdateStats(); }
    private void UseCheckBox_Click(object sender, RoutedEventArgs e) { e.Handled = false; }
    private void Clear_Click(object sender, RoutedEventArgs e) { Photos.Clear(); StatusText.Text = T("AppTitle"); UpdateSelectedPhotoPanel(); UpdateStats(); }

    private void ChooseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = T("OutputDialogTitle"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            OutputDirectoryTextBox.Text = dlg.SelectedPath;
    }

    // ---- Column ----
    private void PhotoGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        if (dep is null) return;
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (parent is null || (parent is not System.Windows.Controls.Primitives.DataGridColumnHeader && dep is not System.Windows.Controls.Primitives.DataGridColumnHeader))
            return;
        while (dep is not null && dep is not System.Windows.Controls.Primitives.DataGridColumnHeader)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is System.Windows.Controls.Primitives.DataGridColumnHeader header && header.Column is not null)
            header.Column.Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells);
    }

    private void PhotoGrid_ColumnReordered(object sender, DataGridColumnEventArgs e) =>
        _settings.ColumnDisplayOrder = PhotoGrid.Columns.Select(c => c.DisplayIndex).ToList();

    private void PhotoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedPhotoPanel();

    private void PhotoGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
        {
            e.Handled = true;
            var idx = PhotoGrid.SelectedIndex;
            if (e.Key == System.Windows.Input.Key.Up && idx > 0) idx--;
            else if (e.Key == System.Windows.Input.Key.Down && idx < Photos.Count - 1) idx++;
            PhotoGrid.SelectedIndex = idx;
            if (PhotoGrid.SelectedItem is not null) PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
        }
    }

    // ---- Panel ----
    private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        var idx = PhotoGrid.SelectedIndex;
        if (idx < 0 || Photos.Count == 0) return;
        new PreviewWindow(Photos.ToArray(), idx) { Owner = this }.ShowDialog();
    }

    private void OpenPhotoMap_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: PhotoItem photo }) OpenMapForPhoto(photo); }
    private void OpenSelectedMap_Click(object sender, RoutedEventArgs e) { if (GetFocusedPhoto() is { } photo) OpenMapForPhoto(photo); }

    private void UseSelectedGpsAsInput_Click(object sender, RoutedEventArgs e)
    {
        if (GetFocusedPhoto() is not { Latitude: not null, Longitude: not null } photo) { StatusText.Text = T("SelectedNoGpsStatus"); return; }
        SetGpsInput(ToGpsInput(photo.Latitude.Value, photo.Longitude.Value, photo.Altitude));
        CoordinateSystemComboBox.SelectedIndex = 0;
    }

    // ---- Location ----
    private void ReferencePhotoButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ReferencePhotoDialog(Photos, _exifToolService, _settings) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
        {
            SetGpsInput(ToGpsInput(dlg.Result.Latitude, dlg.Result.Longitude, dlg.Result.Altitude));
            CoordinateSystemComboBox.SelectedIndex = 0;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ReferenceLoadedStatus"), dlg.ResultFileName ?? "?");
        }
    }

    private void MapPickerButton_Click(object sender, RoutedEventArgs e)
    {
        var focused = GetFocusedPhoto();
        var initial = _currentCoordinate ?? (focused?.Latitude.HasValue == true && focused.Longitude.HasValue
            ? new GpsCoordinate(focused.Latitude.Value, focused.Longitude.Value, focused.Altitude) : null);
        OpenMapEditor(initial, focused?.Latitude.HasValue == true ? T("ExistingGpsNotice") : null);
    }

    private void OpenMapEditor(GpsCoordinate? initial, string? notice)
    {
        var picker = new MapPickerWindow(T("MapPicker"), T("UsePickedLocation"), T("Cancel"), initial, _settings, notice) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedCoordinate is not null)
        {
            CoordinateSystemComboBox.SelectedIndex = 0;
            SetGpsInput(ToGpsInput(picker.SelectedCoordinate.Latitude, picker.SelectedCoordinate.Longitude, picker.SelectedCoordinate.Altitude));
            StatusText.Text = T("MapPickedStatus");
        }
    }

    private void TimeRulesButton_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this, T("TimeRulesExplanation"), T("TimeRules"), MessageBoxButton.OK, MessageBoxImage.Information);
    private void TrackMatchButton_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this, T("TrackMatchExplanation"), T("TrackMatch"), MessageBoxButton.OK, MessageBoxImage.Information);

    // ---- GPS Input ----
    private void GpsInputTextBox_TextChanged(object sender, TextChangedEventArgs? e)
    {
        if (GpsPreviewText is null || WriteButton is null) return;
        if (_isGpsPlaceholderVisible) { _currentCoordinate = null; GpsPreviewText.Text = ""; WriteButton.IsEnabled = false; return; }
        if (GpsParser.TryParse(GpsInputTextBox.Text, out var coordinate, out var error))
        {
            _currentCoordinate = CoordinateTransform.ToWgs84(coordinate, _inputCoordinateSystem);
            GpsPreviewText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            GpsPreviewText.Text = string.Format(CultureInfo.CurrentCulture, T("WillWrite"), _currentCoordinate.Display, _currentCoordinate.LatitudeRef, _currentCoordinate.LongitudeRef);
            WriteButton.IsEnabled = true;
        }
        else { _currentCoordinate = null; GpsPreviewText.Foreground = System.Windows.Media.Brushes.Firebrick; GpsPreviewText.Text = error; WriteButton.IsEnabled = false; }
    }

    private void GpsInputTextBox_GotFocus(object sender, RoutedEventArgs e) { if (_isGpsPlaceholderVisible) { _isGpsPlaceholderVisible = false; GpsInputTextBox.Text = ""; GpsInputTextBox.Foreground = System.Windows.Media.Brushes.Black; } }
    private void GpsInputTextBox_LostFocus(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(GpsInputTextBox.Text)) ShowGpsPlaceholder(); }

    private void ShowGpsPlaceholder()
    {
        if (GpsInputTextBox is null) return;
        _isGpsPlaceholderVisible = true; GpsInputTextBox.Foreground = System.Windows.Media.Brushes.Gray; GpsInputTextBox.Text = T("GpsPlaceholder");
        _currentCoordinate = null; if (GpsPreviewText is not null) GpsPreviewText.Text = ""; if (WriteButton is not null) WriteButton.IsEnabled = false;
    }

    private void SetGpsInput(string value) { _isGpsPlaceholderVisible = false; GpsInputTextBox.Foreground = System.Windows.Media.Brushes.Black; GpsInputTextBox.Text = value; GpsInputTextBox.Focus(); GpsInputTextBox.CaretIndex = GpsInputTextBox.Text.Length; }

    private void CoordinateSystemComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _inputCoordinateSystem = CoordinateSystemComboBox?.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() switch { "gcj02" => CoordinateSystemKind.Gcj02, "bd09" => CoordinateSystemKind.Bd09, _ => CoordinateSystemKind.Wgs84 }
            : CoordinateSystemKind.Wgs84;
        GpsInputTextBox_TextChanged(this, null);
    }

    private void CoordinateSystemHelpButton_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this, T("CoordinateSystemHelp"), T("CoordinateSystemHelpTitle"), MessageBoxButton.OK, MessageBoxImage.Information);

    private static string ToGpsInput(double lat, double lon, double? alt) =>
        alt.HasValue ? FormattableString.Invariant($"{lat}, {lon}, alt={alt}") : FormattableString.Invariant($"{lat}, {lon}");

    // ---- Write Mode ----
    private void WriteModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (WriteModeHintText is null) return;
        WriteModeHintText.Text = GetWriteModeHint();
        var visible = GetWriteMode() != WriteMode.DirectInPlace;
        if (OutputDirContainer is not null) OutputDirContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConvertWriteModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        var visible = GetConvertWriteMode() != WriteMode.DirectInPlace;
        if (ConvertOutputDirContainer is not null) ConvertOutputDirContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Write GPS ----
    private async void WriteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0) { MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information); return; }
        if (_currentCoordinate is null) { MessageBoxShow(T("InvalidCoordinateMessage"), T("InvalidCoordinateTitle"), MessageBoxImage.Warning); return; }
        var mode = GetWriteMode();
        if (mode == WriteMode.CopyToOutputDirectory && string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        { MessageBoxShow(T("MissingOutputMessage"), T("MissingOutputTitle"), MessageBoxImage.Warning); return; }
        var desc = mode switch { WriteMode.CopyToOutputDirectory => string.Format(CultureInfo.CurrentCulture, T("CopyToConfirm"), OutputDirectoryTextBox.Text), WriteMode.DirectInPlace => T("DirectConfirm"), _ => T("BackupConfirm") };
        var gpsCount = selected.Count(p => p.Latitude.HasValue && p.Longitude.HasValue);
        var overwrite = gpsCount > 0 ? "\n" + string.Format(CultureInfo.CurrentCulture, T("OverwriteGpsNotice"), gpsCount) : "";
        if (System.Windows.MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, T("ConfirmWriteMessage"), selected.Length, _currentCoordinate.Display, desc) + overwrite, T("ConfirmWriteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("WritingPhotos"), selected.Length));
        try
        {
            var progress = new Progress<WriteProgress>(UpdateWriteProgress);
            var targets = await _exifToolService.WriteGpsAsync(selected, _currentCoordinate, mode, OutputDirectoryTextBox.Text, progress);
            if (mode is WriteMode.InPlaceWithBackup or WriteMode.DirectInPlace) { UpdateWriteProgress(new("Refreshing...", null)); await _exifToolService.ReadMetadataAsync(selected); }
            foreach (var p in selected) p.Status = "Written";
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("WriteDoneStatus"), targets.Count); UpdateStats();
            ShowToast(T("WriteDoneMessage"));
        }
        catch (Exception ex) { StatusText.Text = ex.Message; System.Windows.MessageBox.Show(this, ex.Message, T("WriteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { ToggleBusy(false); }
    }

    private WriteMode GetWriteMode() => WriteModeComboBox.SelectedIndex switch { 1 => WriteMode.InPlaceWithBackup, 2 => WriteMode.DirectInPlace, _ => WriteMode.CopyToOutputDirectory };
    private WriteMode GetConvertWriteMode() => ConvertWriteModeComboBox.SelectedIndex switch { 1 => WriteMode.InPlaceWithBackup, 2 => WriteMode.DirectInPlace, _ => WriteMode.CopyToOutputDirectory };
    private string GetWriteModeHint() => GetWriteMode() switch { WriteMode.InPlaceWithBackup => T("BackupModeHint"), WriteMode.DirectInPlace => T("DirectModeHint"), _ => T("CopyModeHint") };

    private void MessageBoxShow(string msg, string title, MessageBoxImage icon) => System.Windows.MessageBox.Show(this, msg, title, MessageBoxButton.OK, icon);

    // ---- Date Tools ----
    private void UpdateDateInfo()
    {
        if (DateInfoExif is null) return;
        var photo = GetFocusedPhoto();
        if (photo is null) { DateInfoExif.Text = "拍摄时间: ?"; DateInfoFile.Text = "文件创建: ?"; return; }
        DateInfoExif.Text = $"拍摄时间: {photo.DateTaken ?? "未读取"}";
        DateInfoFile.Text = $"文件创建: {photo.FileCreationTimeDisplay}";
        if (photo.DateTaken is not null && photo.DateTaken.Length >= 19)
        {
            try { DatePickerField.SelectedDate = DateTime.ParseExact(photo.DateTaken[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); } catch { }
            if (photo.DateTaken.Length >= 16) TimeInputBox.Text = photo.DateTaken[11..16];
        }
        else { DatePickerField.SelectedDate = DateTime.Now; TimeInputBox.Text = "00:00"; }
    }

    // ---- Time up/down ----
    private void TimeUpHour_Click(object sender, RoutedEventArgs e) => AdjustTime(60);
    private void TimeDownHour_Click(object sender, RoutedEventArgs e) => AdjustTime(-60);
    private void TimeUpMin_Click(object sender, RoutedEventArgs e) => AdjustTime(1);
    private void TimeDownMin_Click(object sender, RoutedEventArgs e) => AdjustTime(-1);

    private void AdjustTime(int minutes)
    {
        var t = TimeInputBox.Text.Trim();
        if (!TimeSpan.TryParseExact(t, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var ts))
            ts = TimeSpan.FromHours(0);
        ts += TimeSpan.FromMinutes(minutes);
        if (ts.TotalMinutes < 0) ts += TimeSpan.FromHours(24);
        if (ts.TotalMinutes >= 1440) ts -= TimeSpan.FromHours(24);
        TimeInputBox.Text = ts.ToString(@"hh\:mm");
    }

    // ---- Date reference ----
    private void DateRef_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ReferencePhotoDialog(Photos, _exifToolService, _settings) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultFileName is not null)
        {
            var refPhoto = Photos.FirstOrDefault(p => p.FileName == dlg.ResultFileName);
            if (refPhoto?.DateTaken is not null && refPhoto.DateTaken.Length >= 16)
            {
                try { DatePickerField.SelectedDate = DateTime.ParseExact(refPhoto.DateTaken[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); } catch { }
                TimeInputBox.Text = refPhoto.DateTaken[11..16];
            }
        }
    }

    private string GetDateInput()
    {
        var date = DatePickerField.SelectedDate ?? DateTime.Now;
        var time = TimeInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(time)) time = "00:00";
        if (!time.Contains(':')) time = "00:00";
        return $"{date:yyyy:MM:dd} {time}:00";
    }

    // ---- Write date manually ----
    private async void WriteDate_Click(object sender, RoutedEventArgs e)
    {
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0) { MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information); return; }
        var dt = GetDateInput();
        var confirm = System.Windows.MessageBox.Show(this, $"将 {selected.Length} 张照片的拍摄时间写入为:\n{dt}\n\n确定？", "写入日期", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        try { await _exifToolService.WriteDateAsync(selected, dt, default); StatusText.Text = $"已写入日期到 {selected.Length} 张照片。"; UpdateStats(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "写入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ---- Date Check ----
    private static readonly DateTime MinValidDate = new(1970, 1, 1);

    private async void DateCheck_Click(object sender, RoutedEventArgs e)
    {
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0) { MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information); return; }

        var items = new List<DateCheckItem>();
        foreach (var p in selected)
        {
            // A time: EXIF DateTimeOriginal
            DateTime? a = null; bool exifInvalid = false;
            if (p.DateTaken is not null && p.DateTaken.Length >= 19)
            {
                var raw = p.DateTaken[..19];
                if (raw.All(c => c is '0' or ':' or ' ')) { exifInvalid = true; } // "0000:00:00 00:00:00" etc.
                else try { a = DateTime.ParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); } catch { exifInvalid = true; }
            }

            var c = p.FileCreationTime;          // C
            var m = File.GetLastWriteTime(p.Path); // M
            var cValid = c >= MinValidDate;
            var mValid = m >= MinValidDate;

            // B time: earliest valid file time
            DateTime? b = null;
            if (cValid && mValid) b = c < m ? c : m;
            else if (cValid) b = c;
            else if (mValid) b = m;

            var cat = "C";
            var detail = new List<string>();

            // Detect anomalies
            if (!cValid && !mValid)
            {
                cat = "E"; detail.Add("无可用文件时间");
            }
            else if (!cValid && mValid)
            {
                detail.Add("创建时间为空");
            }
            else if (cValid && !mValid)
            {
                detail.Add("修改时间为空");
            }
            else if (m < c) // both valid but modification earlier
            {
                detail.Add("修改早于创建");
            }

            if (exifInvalid)
            {
                cat = b.HasValue ? "F" : "E";
                detail.Add("EXIF异常值(全零)");
            }
            else if (a is null)
            {
                if (b.HasValue)
                {
                    cat = cat == "E" ? "E" : "A";
                    detail.Add("无EXIF");
                }
                else
                {
                    cat = "E";
                    detail.Add("无EXIF");
                }
            }
            else if (b.HasValue && a > b)
            {
                cat = cat == "E" ? "E" : "B";
                detail.Add("EXIF晚于文件");
            }
            else if (a is not null && (!b.HasValue || a <= b))
            {
                cat = "C";
                detail.Add("时间正常");
            }

            if (cat == "E") detail.Add("需手动处理");

            items.Add(new DateCheckItem
            {
                Photo = p,
                ExifDate = a?.ToString("yyyy:MM:dd HH:mm"),
                FileDate = b?.ToString("yyyy:MM:dd HH:mm") ?? "?",
                FileCreation = c,
                FileModification = m,
                Category = cat,
                Detail = string.Join("｜", detail)
            });
        }

        var dlg = new DateCheckDialog(items) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Items to fix: A (no EXIF) + B (EXIF > file) + F (EXIF invalid)
        var toFix = dlg.SelectedIds is null
            ? items.Where(i => i.Category is "A" or "B" or "F" && i.FileDate != "?").ToArray()
            : items.Where(i => (i.Category is "A" or "B" or "F") && i.FileDate != "?" && dlg.SelectedIds!.Contains(i.Photo.FileName)).ToArray();

        if (toFix.Length == 0) { StatusText.Text = "无需修改。"; return; }
        var groups = toFix.GroupBy(i => i.FileDate);
        var count = 0;
        foreach (var grp in groups)
        {
            var batch = grp.Select(i => i.Photo).ToArray();
            try { await _exifToolService.WriteDateAsync(batch, $"{grp.Key}:00", default); count += batch.Length; } catch { }
        }
        StatusText.Text = $"日期校对完成，已写入 {count}/{toFix.Length} 张。"; UpdateStats();
    }

    // ---- Format Tools ----
    private void FixExtensions_Click(object sender, RoutedEventArgs e)
    {
        var toFix = Photos.Where(p => p.IsSelected && p.IsExtensionMismatched).ToArray();
        if (toFix.Length == 0) { MessageBoxShow(T("NoMismatchSelected"), T("NoMismatchTitle"), MessageBoxImage.Information); return; }
        var renamed = 0; var updated = new List<PhotoItem>();
        foreach (var photo in toFix)
        {
            var correctExt = FormatDetector.GetStandardExtension(photo.DetectedFormat);
            var newPath = System.IO.Path.ChangeExtension(photo.Path, correctExt.TrimStart('.'));
            if (string.Equals(photo.Path, newPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(newPath)) continue;
            File.Move(photo.Path, newPath); photo.UpdatePath(newPath);
            photo.SetDetectedFormat(FormatDetector.Detect(newPath)); updated.Add(photo); renamed++;
        }
        if (updated.Count > 0) { StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ExtensionsFixed"), renamed); try { _ = _exifToolService.ReadMetadataAsync(updated); } catch { } }
        UpdateStats();
    }

    private async void ConvertFormat_Click(object sender, RoutedEventArgs e)
    {
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0) { MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information); return; }
        var targetFormat = ConvertFormatComboBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() switch { "png" => Services.ImageFormat.Png, "bmp" => Services.ImageFormat.Bmp, "gif" => Services.ImageFormat.Gif, "tiff" => Services.ImageFormat.Tiff, _ => Services.ImageFormat.Jpeg }
            : Services.ImageFormat.Jpeg;
        var mode = GetConvertWriteMode();
        if (mode == WriteMode.CopyToOutputDirectory && string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        { MessageBoxShow(T("MissingOutputMessage"), T("MissingOutputTitle"), MessageBoxImage.Warning); return; }
        if (System.Windows.MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, T("ConfirmConvertMessage"), selected.Length, FormatDetector.GetFormatLabel(targetFormat)), T("ConfirmConvertTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("ConvertingPhotos"), selected.Length));
        try
        {
            var progress = new Progress<WriteProgress>(UpdateWriteProgress);
            var results = await _imageConversionService.ConvertAsync(selected, targetFormat, mode, OutputDirectoryTextBox.Text, progress);
            foreach (var p in selected) p.Status = "Converted";
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ConvertDoneStatus"), results.Count); UpdateStats();
            ShowToast(string.Format(CultureInfo.CurrentCulture, T("ConvertDoneMessage"), results.Count));
        }
        catch (Exception ex) { StatusText.Text = ex.Message; System.Windows.MessageBox.Show(this, ex.Message, T("ConvertFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { ToggleBusy(false); }
    }

    // ---- Toast ----
    private async void ShowToast(string message)
    {
        _toastCts?.Cancel(); _toastCts = new(); var token = _toastCts.Token;
        if (ToastBorder is null || ToastText is null) return;
        ToastText.Text = message; ToastBorder.Visibility = Visibility.Visible;
        try { await Task.Delay(2500, token); } catch (TaskCanceledException) { return; }
        if (ToastBorder is null) return; ToastBorder.Visibility = Visibility.Collapsed;
    }

    // ---- Panel helpers ----
    private void UpdateSelectedPhotoPanel()
    {
        var photo = GetFocusedPhoto();
        if (photo is null) { SelectedPhotoText.Text = T("NoPhotoSelected"); SelectedGpsText.Text = ""; PhotoThumbnail.Source = null; return; }
        SelectedPhotoText.Text = $"{photo.FileName}\n{photo.CameraModel ?? T("UnknownDevice")}\n{photo.DateTaken ?? T("UnknownDate")}";
        SelectedGpsText.Text = photo.Latitude.HasValue && photo.Longitude.HasValue ? $"GPS: {photo.GpsDisplay}" : T("GpsMissing");
        if (File.Exists(photo.Path))
        {
            try { var src = new System.Windows.Media.Imaging.BitmapImage(); src.BeginInit(); src.DecodePixelWidth = 200; src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; src.UriSource = new Uri(photo.Path); src.EndInit(); src.Freeze(); PhotoThumbnail.Source = src; } catch { PhotoThumbnail.Source = null; }
        }
        else PhotoThumbnail.Source = null;
        UpdateDateInfo();
    }

    private PhotoItem? GetFocusedPhoto() => PhotoGrid.SelectedItem as PhotoItem ?? Photos.FirstOrDefault();
    private IEnumerable<PhotoItem> GetHighlightedPhotos() => PhotoGrid.SelectedItems.OfType<PhotoItem>().ToArray();

    private void OpenMapForPhoto(PhotoItem photo) { PhotoGrid.SelectedItem = photo; photo.IsSelected = true; var init = photo.Latitude.HasValue && photo.Longitude.HasValue ? new GpsCoordinate(photo.Latitude.Value, photo.Longitude.Value, photo.Altitude) : null; OpenMapEditor(init, init is null ? null : T("ExistingGpsNotice")); }

    private void ToggleBusy(bool busy, string? status = null)
    {
        WriteButton.IsEnabled = !busy && _currentCoordinate is not null;
        if (WriteProgressBar is null) return; WriteProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; WriteProgressBar.IsIndeterminate = busy;
        if (status is not null) StatusText.Text = status;
    }

    private void UpdateWriteProgress(WriteProgress p) { StatusText.Text = p.Message; if (WriteProgressBar is null) return; if (p.Percent.HasValue) { WriteProgressBar.IsIndeterminate = false; WriteProgressBar.Value = Math.Clamp(p.Percent.Value, 0, 100); } else WriteProgressBar.IsIndeterminate = true; }

    // ---- Theme ----
    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string theme) App.SetTheme(theme); }

    // ---- Language ----
    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { _language = LanguageComboBox.SelectedItem is ComboBoxItem it && it.Tag?.ToString() == "en" ? AppLanguage.English : AppLanguage.Chinese; ApplyLanguage(); }

    private void ApplyLanguage()
    {
        if (AppTitleText is null) return;
        Title = T("WindowTitle");
        AppTitleText.Text = T("AppTitle"); AppSubtitleText.Text = T("AppSubtitle"); ExifToolStatusText.Text = File.Exists(ExifToolService.DefaultPath) ? T("ExifToolReady") : T("ExifToolMissing");
        AddPhotosButton.Content = T("AddPhotos"); AddFolderButton.Content = T("AddFolder"); IncludeSubfoldersCheckBox.Content = T("IncludeSubfolders");
        SelectAllButton.Content = T("SelectAll"); SelectNoneButton.Content = T("SelectNone"); MarkHighlightedButton.Content = T("MarkHighlighted"); UnmarkHighlightedButton.Content = T("UnmarkHighlighted");
        InvertSelectionButton.Content = T("InvertSelection"); RemoveSelectedButton.Content = T("RemoveSelected"); ClearButton.Content = T("Clear");
        DropHintText.Text = T("DropHint");
        WriteModeLabel.Text = T("WriteModeLabel"); ThemeLabel.Text = T("ThemeLabel"); LanguageLabel.Text = T("LanguageLabel"); ConvertWriteModeLabel.Text = T("WriteModeLabel");
        UseColumn.Header = T("UseColumn"); FileColumn.Header = T("FileColumn"); DeviceColumn.Header = T("DeviceColumn"); CoordinatesColumn.Header = T("CoordinatesColumn"); MapColumn.Header = "Map"; PathColumn.Header = T("PathColumn");
        SelectedPhotoGroup.Text = T("SelectedPhoto");
        GpsExpanderHeader.Text = T("ManualGpsInput"); ManualInputHelpText.Text = T("ManualInputHelp");
        Wgs84CoordinateItem.Content = T("Wgs84Option"); Gcj02CoordinateItem.Content = T("Gcj02Option"); Bd09CoordinateItem.Content = T("Bd09Option");
        CopyModeItem.Content = T("CopyMode"); BackupModeItem.Content = T("BackupMode"); DirectModeItem.Content = T("DirectMode");
        BrowseOutputButton.Content = T("Browse");
        ReferencePhotoButton.Content = T("ReferencePhoto"); MapPickerButton.Content = T("MapPicker"); TimeRulesButton.Content = T("TimeRules"); TrackMatchButton.Content = T("TrackMatch");
        WriteButton.Content = T("WriteButton"); WriteModeHintText.Text = GetWriteModeHint();
        FormatToolsGroup.Text = T("FormatTools"); FixExtensionsButton.Content = T("FixExtensions"); ConvertFormatTitle.Text = T("ConvertFormat"); ConvertFormatButton.Content = T("ConvertFormatBtn");
        DateToolsGroup.Text = T("DateTools"); DateLabel.Text = T("DateLabel");
        WriteDateButton.Content = T("WriteDateBtn"); DateCheckButton.Content = T("DateCheckBtn");
        if (_isGpsPlaceholderVisible) ShowGpsPlaceholder();
        UpdateSelectedPhotoPanel(); UpdateStats(); GpsInputTextBox_TextChanged(this, null);
    }

    private string T(string key) => _language == AppLanguage.Chinese ? Zh(key) : En(key);

    private static string Zh(string key) => key switch
    {
        "WindowTitle" => "Photo Info Editor",
        "AppTitle" => "照片信息编辑器",
        "AppSubtitle" => "照片元数据编辑工具：GPS位置写入、格式检测转换。",
        "ExifToolReady" => "ExifTool ready", "ExifToolMissing" => "ExifTool not found",
        "AddPhotos" => "添加照片", "AddFolder" => "添加文件夹", "IncludeSubfolders" => "包含子文件夹",
        "SelectAll" => "全选", "SelectNone" => "全不选", "MarkHighlighted" => "选中", "UnmarkHighlighted" => "取消选中", "InvertSelection" => "反选", "RemoveSelected" => "移除", "Clear" => "清空",
        "DropHint" => "可拖拽 photos 或 folders 到这里",
        "UseColumn" => "使用", "FileColumn" => "文件", "TakenColumn" => "拍摄时间", "DeviceColumn" => "设备", "CoordinatesColumn" => "坐标", "StatusColumn" => "状态", "PathColumn" => "路径",
        "SelectedPhoto" => "图片信息", "OpenMap" => "打开 Map",
        "ManualGpsInput" => "GPS 编辑", "ManualInputHelp" => "支持十进制度、N/E 前后缀、DMS；海拔可写为 alt=海拔。",
        "GpsPlaceholder" => "输入 GPS 坐标，例如：纬度, 经度 或 N纬度 E经度；可选 alt=海拔",
        "Wgs84Option" => "WGS-84 - GPS/EXIF/Google Earth", "Gcj02Option" => "GCJ-02 - 高德/腾讯/华为 Map", "Bd09Option" => "BD-09 - 百度 Map",
        "CoordinateSystemHelpTitle" => "坐标系怎么选",
        "CoordinateSystemHelp" => "写入 EXIF 时程序会统一转换为 WGS-84。\n\n选 WGS-84：相机 GPS、手机原始 GPS、EXIF、Google Earth、OpenStreetMap、Google Maps 海外区域。\n\n选 GCJ-02：来自高德地图、腾讯地图、华为 Map、中国大陆 Apple 地图、Google Maps 中国大陆道路图层的坐标。\n\n选 BD-09：来自百度地图的坐标。\n\n如果你不确定：从高德/腾讯/华为/国内 Apple 地图复制的坐标，优先选 GCJ-02；从照片 EXIF 或 GPS 设备读出的坐标，选 WGS-84。",
        "WriteModeLabel" => "全局写入方式", "ThemeLabel" => "主题", "LanguageLabel" => "语言",
        "WriteMode" => "写入方式", "CopyMode" => "输出到新目录", "BackupMode" => "原地写入 + Backup", "DirectMode" => "直接写入原文件",
        "OutputDirectory" => "输出目录", "Browse" => "浏览",
        "ReservedFeatures" => "位置工具", "ReferencePhoto" => "参考其他照片", "MapPicker" => "Map 选点", "TimeRules" => "时间段规则", "TrackMatch" => "Track 匹配",
        "WriteButton" => "写入 GPS 到选中照片",
        "CopyModeHint" => "会先复制 photos 到输出目录，只修改副本。",
        "BackupModeHint" => "会先在原图旁创建 .photo-info-backups，再修改原图。",
        "DirectModeHint" => "直接修改原文件，不创建 Backup，也不输出副本。速度最快，但需要你自己确认已有备份。",
        "FolderDialogTitle" => "选择照片文件夹", "OutputDialogTitle" => "选择输出目录",
        "NoNewPhotos" => "没有新增支持的 photos。",
        "ReadingMetadata" => "正在读取 {0} 张 photos 的 metadata...", "ImportedPhotos" => "已导入 {0} 张 photos。",
        "ReadFailedTitle" => "读取失败", "SelectedNoGpsStatus" => "选中照片没有 GPS。",
        "ReferenceNoGpsMessage" => "参考照片没有 GPS 信息。", "ReferenceLoadedStatus" => "已从参考照片 {0} 读取 GPS。",
        "WillWrite" => "将写入：{0} ({1}, {2})", "UsePickedLocation" => "使用此位置", "Cancel" => "取消",
        "MapPickedStatus" => "已从 Map 选点填入 GPS。",
        "ExistingGpsNotice" => "此照片已有 GPS 信息。你可以查看当前位置，也可以拖动或重新选点；确认后会作为新的待写入坐标，写入时将覆盖原 GPS。",
        "OverwriteGpsNotice" => "注意：选中照片中有 {0} 张已有 GPS，继续写入会覆盖它们的原 GPS。",
        "TimeRulesExplanation" => "时间段规则：把某个拍摄时间范围内的 photos 统一写入同一个 GPS。例如 10:00-11:30 都是在景区 A，就批量套用景区 A 的位置。",
        "TrackMatchExplanation" => "Track 匹配：导入手机 GPS 轨迹文件（如 GPX），按照片拍摄时间自动匹配当时所在位置。后续适合旅行全过程自动补 GPS。",
        "NoPhotosSelectedMessage" => "请先选择至少一张 photo。", "NoPhotosSelectedTitle" => "没有选中照片",
        "InvalidCoordinateMessage" => "请输入有效 GPS 坐标。", "InvalidCoordinateTitle" => "坐标无效",
        "MissingOutputMessage" => "请选择输出目录。", "MissingOutputTitle" => "缺少输出目录",
        "CopyToConfirm" => "输出到：{0}", "BackupConfirm" => "原地写入，并先创建 Backup", "DirectConfirm" => "直接写入原文件",
        "ConfirmWriteMessage" => "Photos: {0}\nGPS: {1}\n{2}", "ConfirmWriteTitle" => "确认写入",
        "WritingPhotos" => "正在写入 {0} 张 photos...", "WriteDoneStatus" => "完成，已写入 {0} 张 photos。",
        "WriteDoneMessage" => "GPS 写入完成。", "DoneTitle" => "完成", "WriteFailedTitle" => "写入失败",
        "NoMismatch" => "没有格式不匹配的文件。", "NoMismatchSelected" => "请先选中格式不匹配的照片（筛选➝格式⚠，然后勾选需要更正的）。", "NoMismatchTitle" => "未选中不匹配文件",
        "ExtensionsFixed" => "已更正 {0} 个文件后缀名。",
        "FormatTools" => "格式工具", "FixExtensions" => "更正后缀", "ConvertFormat" => "目标格式", "ConvertFormatBtn" => "转换格式",
        "ConfirmConvertMessage" => "转换 {0} 张照片为 {1} 格式？\n注意：仅选中照片会转换。", "ConfirmConvertTitle" => "确认转换格式",
        "ConvertingPhotos" => "正在转换 {0} 张照片格式...", "ConvertDoneStatus" => "完成，已转换 {0} 张照片。",
        "ConvertDoneMessage" => "格式转换完成 ({0} 张)。", "ConvertFailedTitle" => "转换失败",
        "DateTools" => "日期工具", "DateLabel" => "拍摄日期", "DateFixLabel" => "纠正工具",
        "WriteDateBtn" => "写入日期", "DateCheckBtn" => "日期校对",
        "StatsTotal" => "共 {0} 张", "StatsHasGps" => "{0} 张有 GPS", "StatsSelected" => "选中 {0} 张",
        "NoPhotoSelected" => "未选择照片。", "UnknownDevice" => "未知设备", "UnknownDate" => "未知日期", "GpsMissing" => "GPS: 缺失",
        _ => key
    };

    private static string En(string key) => key switch
    {
        "WindowTitle" => "Photo Info Editor",
        "AppTitle" => "Photo Info Editor",
        "AppSubtitle" => "Photo metadata editing tool: GPS writing, format detection and conversion.",
        "ExifToolReady" => "ExifTool ready", "ExifToolMissing" => "ExifTool not found",
        "AddPhotos" => "Add Photos", "AddFolder" => "Add Folder", "IncludeSubfolders" => "Include subfolders",
        "SelectAll" => "Select All", "SelectNone" => "Select None", "MarkHighlighted" => "Select", "UnmarkHighlighted" => "Unselect",
        "InvertSelection" => "Invert", "RemoveSelected" => "Remove", "Clear" => "Clear",
        "DropHint" => "Drop photos or folders here",
        "UseColumn" => "Use", "FileColumn" => "File", "TakenColumn" => "Taken", "DeviceColumn" => "Device", "CoordinatesColumn" => "Coordinates", "StatusColumn" => "Status", "PathColumn" => "Path",
        "SelectedPhoto" => "Photo Info", "OpenMap" => "Open Map",
        "ManualGpsInput" => "GPS Editing", "ManualInputHelp" => "Supports decimal degrees, N/E prefixes or suffixes, DMS, and optional altitude like alt=altitude.",
        "GpsPlaceholder" => "Enter GPS coordinates, e.g. latitude, longitude or Nlatitude Elongitude; optional alt=altitude",
        "Wgs84Option" => "WGS-84 - GPS/EXIF/Google Earth", "Gcj02Option" => "GCJ-02 - AMap/Tencent/Huawei Map", "Bd09Option" => "BD-09 - Baidu Map",
        "CoordinateSystemHelpTitle" => "Which coordinate system?",
        "CoordinateSystemHelp" => "The app always writes EXIF as WGS-84.\n\nChoose WGS-84 for camera GPS, raw phone GPS, EXIF, Google Earth, OpenStreetMap, and Google Maps outside mainland China.\n\nChoose GCJ-02 for coordinates from AMap, Tencent Maps, Huawei Map, Apple Maps in mainland China, and Google Maps China street-map coordinates.\n\nChoose BD-09 for Baidu Map coordinates.\n\nIf unsure: coordinates copied from AMap/Tencent/Huawei/domestic Apple Maps should usually use GCJ-02; coordinates read from EXIF or a GPS device should use WGS-84.",
        "WriteModeLabel" => "Write Mode", "ThemeLabel" => "Theme", "LanguageLabel" => "Language",
        "WriteMode" => "Write Mode", "CopyMode" => "Copy to output directory", "BackupMode" => "Write in place with Backup", "DirectMode" => "Direct write in place",
        "OutputDirectory" => "Output directory", "Browse" => "Browse",
        "ReservedFeatures" => "Location Tools", "ReferencePhoto" => "Reference Photo", "MapPicker" => "Map Picker", "TimeRules" => "Time Rules", "TrackMatch" => "Track Match",
        "WriteButton" => "Write GPS To Selected Photos",
        "CopyModeHint" => "Photos are copied to the output directory first. Only the copies are modified.",
        "BackupModeHint" => "A .photo-info-backups folder is created beside the source photos before originals are modified.",
        "DirectModeHint" => "Original files are modified directly. No Backup and no copied output are created. Fastest, but use only when you already have a backup.",
        "FolderDialogTitle" => "Choose a photo folder", "OutputDialogTitle" => "Choose output directory",
        "NoNewPhotos" => "No new supported photos were added.",
        "ReadingMetadata" => "Reading metadata for {0} photos...", "ImportedPhotos" => "Imported {0} photos.",
        "ReadFailedTitle" => "Read failed", "SelectedNoGpsStatus" => "Selected photo has no GPS data.",
        "ReferenceNoGpsMessage" => "The reference photo has no GPS data.", "ReferenceLoadedStatus" => "Loaded GPS from reference photo {0}.",
        "WillWrite" => "Will write: {0} ({1}, {2})", "UsePickedLocation" => "Use This Location", "Cancel" => "Cancel",
        "MapPickedStatus" => "Filled GPS from Map Picker.",
        "ExistingGpsNotice" => "This photo already has GPS data. You can review it, drag the marker, or pick a new point. Confirming will fill a new pending coordinate; writing will overwrite the old GPS.",
        "OverwriteGpsNotice" => "Note: {0} selected photos already have GPS data. Writing will overwrite their existing GPS.",
        "TimeRulesExplanation" => "Time Rules: apply one GPS location to photos within a shooting time range, for example all photos from 10:00-11:30 at scenic spot A.",
        "TrackMatchExplanation" => "Track Match: import a phone GPS track file such as GPX, then match each photo to the position recorded at its shooting time.",
        "NoPhotosSelectedMessage" => "Select at least one photo first.", "NoPhotosSelectedTitle" => "No photos selected",
        "InvalidCoordinateMessage" => "Enter a valid GPS coordinate.", "InvalidCoordinateTitle" => "Invalid coordinate",
        "MissingOutputMessage" => "Choose an output directory.", "MissingOutputTitle" => "Missing output directory",
        "CopyToConfirm" => "Copy to: {0}", "BackupConfirm" => "Write in place after creating backups", "DirectConfirm" => "Write directly to original files without Backup",
        "ConfirmWriteMessage" => "Photos: {0}\nGPS: {1}\n{2}", "ConfirmWriteTitle" => "Confirm write",
        "WritingPhotos" => "Writing {0} photos...", "WriteDoneStatus" => "Done. Wrote GPS to {0} photos.",
        "WriteDoneMessage" => "GPS write complete.", "DoneTitle" => "Done", "WriteFailedTitle" => "Write failed",
        "NoMismatch" => "No files with mismatched extensions.", "NoMismatchSelected" => "Select mismatched photos first (filter by 格式⚠, then check the ones to fix).", "NoMismatchTitle" => "No mismatched files selected",
        "ExtensionsFixed" => "Renamed {0} file extensions.",
        "FormatTools" => "Format Tools", "FixExtensions" => "Fix Extensions", "ConvertFormat" => "Target Format", "ConvertFormatBtn" => "Convert",
        "ConfirmConvertMessage" => "Convert {0} photos to {1}?", "ConfirmConvertTitle" => "Confirm convert",
        "ConvertingPhotos" => "Converting {0} photos...", "ConvertDoneStatus" => "Done. Converted {0} photos.",
        "ConvertDoneMessage" => "Format conversion done ({0} photos).", "ConvertFailedTitle" => "Convert failed",
        "DateTools" => "Date Tools", "DateLabel" => "Capture Date", "DateFixLabel" => "Fix Tools",
        "WriteDateBtn" => "Write Date", "DateCheckBtn" => "Date Check",
        "StatsTotal" => "Total: {0}", "StatsHasGps" => "GPS: {0}", "StatsSelected" => "Selected: {0}",
        "NoPhotoSelected" => "No photo selected.", "UnknownDevice" => "Unknown device", "UnknownDate" => "Unknown date", "GpsMissing" => "GPS: missing",
        _ => key
    };

    private enum AppLanguage { Chinese, English }
}
