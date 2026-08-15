using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;
using WinForms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using Button = System.Windows.Controls.Button;

namespace PhotoLocationEditor.App;

public partial class MainWindow : Window
{
    private const int ThumbnailCacheLimit = 120;

    private readonly ExifToolService _exifToolService = new(ExifToolService.DefaultPath);
    private readonly ImageConversionService _imageConversionService;
    private readonly AppSettingsService _settingsService = new();
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private GpsCoordinate? _currentCoordinate;
    private AppLanguage _language = AppLanguage.Chinese;
    private bool _isGpsPlaceholderVisible;
    private bool _isBusy;
    private CoordinateSystemKind _inputCoordinateSystem = CoordinateSystemKind.Wgs84;
    private FilterMode _filterMode = FilterMode.All;
    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _busyCts;
    private string? _thumbnailRequestPath;

    private enum FilterMode { All, NoGps, HasGps, Fuji, Huawei, ExtensionMismatch }

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
        if (ThemeComboBox.SelectedIndex > 0)
            App.SetTheme(_settings.Theme);

        RestoreWindowState();

        // Language first, then the remaining controls whose labels depend on it.
        LanguageComboBox.SelectedIndex = _settings.LastLanguage == "en" ? 1 : 0;
        ApplyLanguage();

        // Legacy write-mode migration:
        // old: 0 = copy, 1 = backup, 2 = direct. Backup is removed, map it to safe copy.
        RestoreWriteMode(_settings.LastWriteMode);

        if (!string.IsNullOrEmpty(_settings.LastOutputDirectory))
            OutputDirectoryTextBox.Text = _settings.LastOutputDirectory;
        if (_settings.ColumnDisplayOrder is not null && _settings.ColumnDisplayOrder.Count == PhotoGrid.Columns.Count)
            for (var i = 0; i < PhotoGrid.Columns.Count; i++)
                PhotoGrid.Columns[i].DisplayIndex = _settings.ColumnDisplayOrder[i];

        ShowGpsPlaceholder();
        UpdateSelectedPhotoPanel();
        UpdateStats();
        FilterSearchBox.Text = _settings.LastFilterText ?? "";
        FilterSearchBox.TextChanged += FilterSearchBox_TextChanged;
        WriteModeOption_Checked(this, null);
        ConvertWriteModeOption_Checked(this, null);
        UpdateFilterChips();
    }

    public ObservableCollection<PhotoItem> Photos { get; }

    // ---- Persistence ----
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _busyCts?.Cancel();
        try
        {
            if (ThemeComboBox.SelectedItem is ComboBoxItem themeItem)
                _settings.Theme = themeItem.Tag?.ToString() ?? "light";
            _settings.LastWriteMode = GetWriteMode() == WriteMode.DirectInPlace ? 2 : 0;
            _settings.LastOutputDirectory = OutputDirectoryTextBox.Text;
            _settings.LastLanguage = _language == AppLanguage.English ? "en" : "zh";
            _settings.LastFilterText = FilterSearchBox.Text;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowStateValue = (int)WindowState;
            _settingsService.Save(_settings);
        }
        catch
        {
            // Never block application shutdown because of a settings write failure.
        }
    }

    private void RestoreWindowState()
    {
        try
        {
            if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue &&
                _settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
            {
                var candidate = new Rect(
                    _settings.WindowLeft.Value,
                    _settings.WindowTop.Value,
                    Math.Max(MinWidth, _settings.WindowWidth.Value),
                    Math.Max(MinHeight, _settings.WindowHeight.Value));
                if (IsVisibleOnAnyScreen(candidate))
                {
                    Left = _settings.WindowLeft.Value;
                    Top = _settings.WindowTop.Value;
                    Width = _settings.WindowWidth.Value;
                    Height = _settings.WindowHeight.Value;
                }
            }

            if (_settings.WindowStateValue == (int)WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch
        {
            // Fall back to the XAML defaults.
        }
    }

    private static bool IsVisibleOnAnyScreen(Rect r)
    {
        return r.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
               r.Right > SystemParameters.VirtualScreenLeft &&
               r.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight &&
               r.Bottom > SystemParameters.VirtualScreenTop;
    }

    // ---- Keyboard global ----
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextEntryFocused())
            return;

        if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.A: SelectAll_Click(sender, e); e.Handled = true; return;
                case Key.I: InvertSelection_Click(sender, e); e.Handled = true; return;
                case Key.D0: SelectNone_Click(sender, e); e.Handled = true; return;
                case Key.W: WriteButton_Click(sender, e); e.Handled = true; return;
                case Key.OemPlus:
                case Key.Add: MarkHighlighted_Click(sender, e); e.Handled = true; return;
                case Key.OemMinus:
                case Key.Subtract: UnmarkHighlighted_Click(sender, e); e.Handled = true; return;
            }
        }

        if (e.Key == Key.Delete)
        {
            RemoveSelected_Click(sender, e);
            e.Handled = true;
        }
    }

    private static bool IsTextEntryFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or PasswordBox;
    }

    // ---- Stats ----
    private void UpdateMismatchStats()
    {
        if (MismatchStatsText is null || FixExtensionsButton is null)
            return;

        var mismatched = Photos.Where(p => p.IsExtensionMismatched).ToArray();
        if (mismatched.Length == 0)
        {
            MismatchStatsText.Text = "✓ " + T("NoMismatch");
            MismatchStatsText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            FixExtensionsButton.IsEnabled = false;
            return;
        }

        var total = Photos.Count;
        var pct = total > 0 ? mismatched.Length * 100.0 / total : 0.0;
        var byType = mismatched.GroupBy(p => FormatDetector.GetFormatLabel(p.DetectedFormat));
        var typeStr = string.Join(", ", byType.Select(g => $"{g.Key}:{g.Count()}"));
        MismatchStatsText.Text = $"⚠ {mismatched.Length} / {total} ({pct:F1}%) — {typeStr}";
        MismatchStatsText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        FixExtensionsButton.IsEnabled = true;
    }

    private void UpdateStats()
    {
        if (StatsTotalText is null)
            return;
        var total = Photos.Count;
        var gps = Photos.Count(p => p.Latitude.HasValue && p.Longitude.HasValue);
        var selected = Photos.Count(p => p.IsSelected);
        StatsTotalText.Text = string.Format(CultureInfo.CurrentCulture, T("StatsTotal"), total);
        StatsGpsText.Text = string.Format(CultureInfo.CurrentCulture, T("StatsHasGps"), gps);
        StatsSelectedText.Text = string.Format(CultureInfo.CurrentCulture, T("StatsSelected"), selected);
        UpdateMismatchStats();
    }

    // ---- Filter ----
    private void SetFilter(FilterMode mode)
    {
        _filterMode = mode;
        ApplyFilter();
        UpdateFilterChips();
    }

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
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return photo.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               photo.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (photo.CameraModel?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (photo.CameraMake?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (photo.DateTaken?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               photo.FormatDisplay.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateFilterChips()
    {
        if (FilterAllButton is null)
            return;
        var normal = (Style)FindResource("ChipButton");
        var active = (Style)FindResource("ChipButtonActive");
        FilterAllButton.Style = _filterMode == FilterMode.All ? active : normal;
        FilterNoGpsButton.Style = _filterMode == FilterMode.NoGps ? active : normal;
        FilterHasGpsButton.Style = _filterMode == FilterMode.HasGps ? active : normal;
        FilterFujiButton.Style = _filterMode == FilterMode.Fuji ? active : normal;
        FilterHuaweiButton.Style = _filterMode == FilterMode.Huawei ? active : normal;
        FilterMismatchButton.Style = _filterMode == FilterMode.ExtensionMismatch ? active : normal;
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
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Image/Video files|*.jpg;*.jpeg;*.heic;*.heif;*.hif;*.png;*.webp;*.mp4;*.mov;*.avi;*.mkv;*.3gp;*.m4v;*.wmv;*.mts;*.m2ts|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            await AddPathsAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = T("FolderDialogTitle"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            var option = IncludeSubfoldersCheckBox.IsChecked == true
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            var scan = await Task.Run(() =>
            {
                var all = Directory.EnumerateFiles(dlg.SelectedPath, "*", option).ToArray();
                var sup = all.Where(ExifToolService.IsSupportedImage).ToArray();
                var unsup = all.Where(f => !ExifToolService.IsSupportedImage(f))
                    .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                    .Select(g => $"{g.Key}:{g.Count()}")
                    .ToArray();
                return (All: all, Supported: sup, Unsupported: unsup);
            });
            var allFiles = scan.All;
            var supported = scan.Supported;
            var unsupported = scan.Unsupported;

            if (supported.Length > 0)
                await AddPathsAsync(supported);

            if (supported.Length > 0 && unsupported.Length > 0)
                System.Windows.MessageBox.Show(this,
                    string.Format(CultureInfo.CurrentCulture, T("ImportReportPartial"), supported.Length, allFiles.Length - supported.Length, string.Join(", ", unsupported)),
                    T("ImportReportTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            else if (supported.Length == 0 && unsupported.Length > 0)
                System.Windows.MessageBox.Show(this,
                    string.Format(CultureInfo.CurrentCulture, T("ImportReportNone"), allFiles.Length, string.Join(", ", unsupported)),
                    T("ImportReportTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                StatusText.Text = T("NoNewPhotos");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, T("ImportReportTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (dropped is null || dropped.Length == 0)
            return;
        var recursive = IncludeSubfoldersCheckBox.IsChecked == true;
        try
        {
            var paths = await Task.Run(() => ExpandDroppedPaths(dropped, recursive).ToArray());
            await AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, T("ImportReportTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static IEnumerable<string> ExpandDroppedPaths(IEnumerable<string> paths, bool recursive)
    {
        foreach (var path in paths)
        {
            if (ExifToolService.IsSupportedImage(path) && File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(path, "*", option).Where(ExifToolService.IsSupportedImage))
                    yield return file;
            }
        }
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var sw = Stopwatch.StartNew();
        var existing = Photos.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = paths
            .Where(File.Exists)
            .Where(ExifToolService.IsSupportedImage)
            .Where(path => existing.Add(path))
            .Select(path => new PhotoItem(path))
            .ToArray();

        foreach (var item in added)
        {
            item.PropertyChanged += OnPhotoPropertyChanged;
            Photos.Add(item);
        }

        if (added.Length == 0)
        {
            StatusText.Text = T("NoNewPhotos");
            return;
        }

        StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ReadingMetadata"), added.Length);
        try
        {
            await _exifToolService.ReadMetadataAsync(added);

            // Magic-byte detection and filesystem metadata are offloaded from the UI thread.
            await Task.Run(() =>
            {
                foreach (var photo in added)
                {
                    photo.SetDetectedFormat(FormatDetector.Detect(photo.Path));
                    try { photo.SetFileCreationTime(File.GetCreationTime(photo.Path)); }
                    catch { /* file may have been removed while importing */ }
                }
            });

            sw.Stop();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ImportedPhotos"), added.Length) +
                              $" ({sw.Elapsed.TotalSeconds:0.0}s)";
            UpdateSelectedPhotoPanel();
            UpdateStats();
        }
        catch (Exception ex)
        {
            foreach (var item in added)
                item.Status = T("ReadFailedStatus");
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, T("ReadFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPhotoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoItem.IsSelected))
            Dispatcher.InvokeAsync(UpdateStats);
    }

    // ---- Selection ----
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Photos) p.IsSelected = true;
        UpdateStats();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Photos) p.IsSelected = false;
        UpdateStats();
    }

    private void MarkHighlighted_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in GetHighlightedPhotos()) p.IsSelected = true;
        UpdateStats();
    }

    private void UnmarkHighlighted_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in GetHighlightedPhotos()) p.IsSelected = false;
        UpdateStats();
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Photos) p.IsSelected = !p.IsSelected;
        UpdateStats();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0)
            return;

        var confirm = System.Windows.MessageBox.Show(
            this,
            string.Format(CultureInfo.CurrentCulture, T("ConfirmRemoveSelected"), selected.Length),
            T("ConfirmRemoveTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        for (var i = Photos.Count - 1; i >= 0; i--)
            if (Photos[i].IsSelected)
                Photos.RemoveAt(i);

        UpdateSelectedPhotoPanel();
        UpdateStats();
        StatusText.Text = T("NoNewPhotos");
    }

    private void UseCheckBox_Click(object sender, RoutedEventArgs e)
    {
        // TwoWay binding handles the value; UpdateStats is wired through PropertyChanged.
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (Photos.Count == 0)
            return;
        var confirm = System.Windows.MessageBox.Show(
            this,
            T("ConfirmClear"),
            T("ConfirmClearTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        Photos.Clear();
        _thumbnailCache.Clear();
        StatusText.Text = T("AppTitle");
        UpdateSelectedPhotoPanel();
        UpdateStats();
    }

    private void ChooseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog { Description = T("OutputDialogTitle"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK)
            return;

        if (ReferenceEquals(sender, ConvertBrowseButton))
            ConvertOutputTextBox.Text = dlg.SelectedPath;
        else
            OutputDirectoryTextBox.Text = dlg.SelectedPath;
    }

    // ---- Columns and grid ----
    private void PhotoGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        if (dep is null)
            return;

        while (dep is not null && dep is not DataGridColumnHeader)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is DataGridColumnHeader header && header.Column is not null)
            header.Column.Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells);
    }

    private void PhotoGrid_ColumnReordered(object sender, DataGridColumnEventArgs e) =>
        _settings.ColumnDisplayOrder = PhotoGrid.Columns.Select(c => c.DisplayIndex).ToList();

    private void PhotoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectedPhotoPanel();

    private void PhotoGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Up and not Key.Down)
            return;

        e.Handled = true;
        var idx = PhotoGrid.SelectedIndex;
        var count = PhotoGrid.Items.Count;
        if (e.Key == Key.Up && idx > 0) idx--;
        else if (e.Key == Key.Down && idx < count - 1) idx++;
        PhotoGrid.SelectedIndex = idx;
        if (PhotoGrid.SelectedItem is not null)
            PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
    }

    // ---- Selected photo panel ----
    private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        var photo = GetFocusedPhoto();
        if (photo is null)
            return;
        new PreviewWindow(Photos.ToArray(), Math.Max(0, Photos.IndexOf(photo)), _language == AppLanguage.English) { Owner = this }.ShowDialog();
    }

    private void OpenPhotoMap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PhotoItem photo })
            OpenMapForPhoto(photo);
    }

    private void OpenSelectedMap_Click(object sender, RoutedEventArgs e)
    {
        if (GetFocusedPhoto() is { } photo)
            OpenMapForPhoto(photo);
    }

    private void UseSelectedGpsAsInput_Click(object sender, RoutedEventArgs e)
    {
        var photo = GetFocusedPhoto();
        if (photo is null || photo.Latitude is null || photo.Longitude is null)
        {
            StatusText.Text = T("SelectedNoGpsStatus");
            return;
        }

        SetGpsInput(ToGpsInput(photo.Latitude.Value, photo.Longitude.Value, photo.Altitude));
        CoordinateSystemComboBox.SelectedIndex = 0;
    }

    // ---- Location ----
    private void ReferencePhotoButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ReferencePhotoDialog(Photos, _exifToolService, _settings, _language == AppLanguage.English) { Owner = this };
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
            ? new GpsCoordinate(focused.Latitude.Value, focused.Longitude.Value, focused.Altitude)
            : null);
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

    // ---- GPS input ----
    private void GpsInputTextBox_TextChanged(object sender, TextChangedEventArgs? e)
    {
        if (GpsPreviewText is null || WriteButton is null)
            return;

        if (_isGpsPlaceholderVisible)
        {
            _currentCoordinate = null;
            GpsPreviewText.Text = "";
            WriteButton.IsEnabled = false;
            return;
        }

        if (GpsParser.TryParse(GpsInputTextBox.Text, out var coordinate, out var error))
        {
            _currentCoordinate = CoordinateTransform.ToWgs84(coordinate, _inputCoordinateSystem);
            GpsPreviewText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            GpsPreviewText.Text = string.Format(
                CultureInfo.CurrentCulture,
                T("WillWrite"),
                _currentCoordinate.Display,
                _currentCoordinate.LatitudeRef,
                _currentCoordinate.LongitudeRef);
            WriteButton.IsEnabled = !_isBusy;
        }
        else
        {
            _currentCoordinate = null;
            GpsPreviewText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            GpsPreviewText.Text = error;
            WriteButton.IsEnabled = false;
        }
    }

    private void GpsInputTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_isGpsPlaceholderVisible)
        {
            _isGpsPlaceholderVisible = false;
            GpsInputTextBox.Text = "";
            GpsInputTextBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
        }
    }

    private void GpsInputTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GpsInputTextBox.Text))
            ShowGpsPlaceholder();
    }

    private void ShowGpsPlaceholder()
    {
        if (GpsInputTextBox is null)
            return;
        _isGpsPlaceholderVisible = true;
        GpsInputTextBox.SetResourceReference(TextBox.ForegroundProperty, "TextMuted");
        GpsInputTextBox.Text = T("GpsPlaceholder");
        _currentCoordinate = null;
        if (GpsPreviewText is not null)
            GpsPreviewText.Text = "";
        if (WriteButton is not null)
            WriteButton.IsEnabled = false;
    }

    private void SetGpsInput(string value)
    {
        _isGpsPlaceholderVisible = false;
        GpsInputTextBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
        GpsInputTextBox.Text = value;
        GpsInputTextBox.Focus();
        GpsInputTextBox.CaretIndex = GpsInputTextBox.Text.Length;
    }

    private void CoordinateSystemComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _inputCoordinateSystem = CoordinateSystemComboBox?.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() switch
            {
                "gcj02" => CoordinateSystemKind.Gcj02,
                "bd09" => CoordinateSystemKind.Bd09,
                _ => CoordinateSystemKind.Wgs84
            }
            : CoordinateSystemKind.Wgs84;
        GpsInputTextBox_TextChanged(this, null);
    }

    private void CoordinateSystemHelpButton_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this, T("CoordinateSystemHelp"), T("CoordinateSystemHelpTitle"), MessageBoxButton.OK, MessageBoxImage.Information);

    private static string ToGpsInput(double lat, double lon, double? alt) =>
        alt.HasValue
            ? FormattableString.Invariant($"{lat}, {lon}, alt={alt}")
            : FormattableString.Invariant($"{lat}, {lon}");

    // ---- Write mode (Backup removed: Copy / Direct only) ----
    private void WriteModeOption_Checked(object sender, RoutedEventArgs? e)
    {
        if (WriteModeHintText is null)
            return;
        WriteModeHintText.Text = GetWriteModeHint();
        if (OutputDirContainer is not null)
            OutputDirContainer.Visibility = GetWriteMode() == WriteMode.CopyToOutputDirectory
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ConvertWriteModeOption_Checked(object sender, RoutedEventArgs? e)
    {
        if (ConvertOutputDirContainer is not null)
            ConvertOutputDirContainer.Visibility = GetConvertWriteMode() == WriteMode.CopyToOutputDirectory
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RestoreWriteMode(int legacy)
    {
        if (legacy == 2)
        {
            DirectModeItem.IsChecked = true;
            CopyModeItem.IsChecked = false;
        }
        else
        {
            // Legacy backup mode (1) maps to copy for safety.
            CopyModeItem.IsChecked = true;
            DirectModeItem.IsChecked = false;
        }
    }

    private WriteMode GetWriteMode() =>
        CopyModeItem.IsChecked == true ? WriteMode.CopyToOutputDirectory : WriteMode.DirectInPlace;

    private WriteMode GetConvertWriteMode() =>
        ConvertCopyModeItem.IsChecked == true ? WriteMode.CopyToOutputDirectory : WriteMode.DirectInPlace;

    private string GetWriteModeHint() => GetWriteMode() switch
    {
        WriteMode.CopyToOutputDirectory => T("CopyModeHint"),
        _ => T("DirectModeHint")
    };

    private string GetWriteModeDescription(WriteMode mode) => mode switch
    {
        WriteMode.CopyToOutputDirectory => string.Format(CultureInfo.CurrentCulture, T("CopyToConfirm"), OutputDirectoryTextBox.Text),
        _ => T("DirectConfirm")
    };

    private void MessageBoxShow(string msg, string title, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(this, msg, title, MessageBoxButton.OK, icon);

    // ---- Write GPS ----
    private async void WriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information);
            return;
        }
        if (_currentCoordinate is null)
        {
            MessageBoxShow(T("InvalidCoordinateMessage"), T("InvalidCoordinateTitle"), MessageBoxImage.Warning);
            return;
        }

        var mode = GetWriteMode();
        if (mode == WriteMode.CopyToOutputDirectory && string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        {
            MessageBoxShow(T("MissingOutputMessage"), T("MissingOutputTitle"), MessageBoxImage.Warning);
            return;
        }

        var desc = GetWriteModeDescription(mode);
        var gpsCount = selected.Count(p => p.Latitude.HasValue && p.Longitude.HasValue);
        var overwrite = gpsCount > 0
            ? "\n" + string.Format(CultureInfo.CurrentCulture, T("OverwriteGpsNotice"), gpsCount)
            : "";
        if (System.Windows.MessageBox.Show(
                this,
                string.Format(CultureInfo.CurrentCulture, T("ConfirmWriteMessage"), selected.Length, _currentCoordinate.Display, desc) + overwrite,
                T("ConfirmWriteTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = _busyCts.Token;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("WritingPhotos"), selected.Length));

        try
        {
            var sw = Stopwatch.StartNew();
            var progress = new Progress<WriteProgress>(UpdateWriteProgress);
            var result = await _exifToolService.WriteGpsAsync(selected, _currentCoordinate, mode, OutputDirectoryTextBox.Text, progress, token);

            if (mode == WriteMode.DirectInPlace)
            {
                UpdateWriteProgress(new WriteProgress(T("Refreshing"), null));
                await _exifToolService.ReadMetadataAsync(selected, token);
            }

            foreach (var p in selected)
                p.Status = T("WrittenStatus");

            sw.Stop();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("WriteDoneStatus"), result.Targets.Count) +
                              (result.SkippedFileNames.Count > 0
                                  ? "  " + string.Format(CultureInfo.CurrentCulture, T("SkippedReadOnly"), result.SkippedFileNames.Count)
                                  : "") +
                              $" ({sw.Elapsed.TotalSeconds:0.0}s)";
            UpdateStats();
            ShowToast(T("WriteDoneMessage"));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("CanceledStatus");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, T("WriteFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    // ---- Date tools ----
    private void UpdateDateInfo()
    {
        if (DateInfoExif is null)
            return;
        var photo = GetFocusedPhoto();
        if (photo is null)
        {
            DateInfoExif.Text = T("NoPhotoSelected");
            DateInfoFile.Text = "";
            DatePickerField.SelectedDate = null;
            TimeInputBox.Text = "00:00";
            return;
        }

        DateInfoExif.Text = $"{T("DateInfoExif")}: {photo.DateTaken ?? T("DateNotRead")}";
        DateInfoFile.Text = $"{T("DateInfoFileCreated")}: {photo.FileCreationTimeDisplay}";

        if (photo.DateTaken is not null && photo.DateTaken.Length >= 19)
        {
            try { DatePickerField.SelectedDate = DateTime.ParseExact(photo.DateTaken[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); }
            catch { DatePickerField.SelectedDate = null; }
            if (photo.DateTaken.Length >= 16)
                TimeInputBox.Text = photo.DateTaken[11..16];
        }
        else
        {
            DatePickerField.SelectedDate = DateTime.Now;
            TimeInputBox.Text = "00:00";
        }
    }

    private void TimeUpHour_Click(object sender, RoutedEventArgs e) => AdjustTime(60);
    private void TimeDownHour_Click(object sender, RoutedEventArgs e) => AdjustTime(-60);
    private void TimeUpMin_Click(object sender, RoutedEventArgs e) => AdjustTime(1);
    private void TimeDownMin_Click(object sender, RoutedEventArgs e) => AdjustTime(-1);

    private void AdjustTime(int minutes)
    {
        var t = TimeInputBox.Text.Trim();
        if (!TimeSpan.TryParseExact(t, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var ts))
            ts = TimeSpan.Zero;
        ts += TimeSpan.FromMinutes(minutes);
        if (ts.TotalMinutes < 0) ts += TimeSpan.FromHours(24);
        if (ts.TotalMinutes >= 1440) ts -= TimeSpan.FromHours(24);
        TimeInputBox.Text = ts.ToString(@"hh\:mm");
    }

    private void DateRef_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ReferencePhotoDialog(Photos, _exifToolService, _settings, _language == AppLanguage.English) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultFileName is not null)
        {
            var refPhoto = Photos.FirstOrDefault(p => p.FileName == dlg.ResultFileName);
            if (refPhoto?.DateTaken is not null && refPhoto.DateTaken.Length >= 16)
            {
                try { DatePickerField.SelectedDate = DateTime.ParseExact(refPhoto.DateTaken[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); }
                catch { /* leave current */ }
                TimeInputBox.Text = refPhoto.DateTaken[11..16];
            }
        }
    }

    private bool TryGetDateInput(out string dateTimeOriginal, out string error)
    {
        dateTimeOriginal = "";
        error = "";
        var date = DatePickerField.SelectedDate;
        if (date is null)
        {
            error = T("InvalidDateMessage");
            return false;
        }

        var time = TimeInputBox.Text.Trim();
        if (!TimeSpan.TryParseExact(time, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var ts))
        {
            error = T("InvalidTimeMessage");
            return false;
        }

        dateTimeOriginal = $"{date.Value:yyyy:MM:dd} {ts.ToString(@"hh\:mm")}:00";
        return true;
    }

    private async void WriteDate_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information);
            return;
        }

        var mode = GetWriteMode();
        if (mode == WriteMode.CopyToOutputDirectory && string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        {
            MessageBoxShow(T("MissingOutputMessage"), T("MissingOutputTitle"), MessageBoxImage.Warning);
            return;
        }

        if (!TryGetDateInput(out var dt, out var inputError))
        {
            MessageBoxShow(inputError, T("InvalidDateTitle"), MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            string.Format(CultureInfo.CurrentCulture, T("ConfirmWriteDateMessage"), selected.Length, dt, GetWriteModeDescription(mode)),
            T("ConfirmWriteDateTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = _busyCts.Token;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("WritingDates"), selected.Length));

        try
        {
            var sw = Stopwatch.StartNew();
            var progress = new Progress<WriteProgress>(UpdateWriteProgress);
            var dateResult = await _exifToolService.WriteDateAsync(selected, dt, mode, OutputDirectoryTextBox.Text, progress, token);

            if (mode == WriteMode.DirectInPlace)
            {
                UpdateWriteProgress(new WriteProgress(T("Refreshing"), null));
                await _exifToolService.ReadMetadataAsync(selected, token);
            }

            sw.Stop();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("WriteDateDoneStatus"), dateResult.Targets.Count) +
                                (dateResult.SkippedFileNames.Count > 0
                                    ? "  " + string.Format(CultureInfo.CurrentCulture, T("SkippedReadOnly"), dateResult.SkippedFileNames.Count)
                                    : "") +
                              $" ({sw.Elapsed.TotalSeconds:0.0}s)";
            UpdateStats();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("CanceledStatus");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, T("WriteDateFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    // ---- Date check ----
    private static readonly DateTime MinValidDate = new(1970, 1, 1);

    private static List<DateCheckItem> BuildDateCheckItems(IReadOnlyList<PhotoItem> selected)
    {
        var items = new List<DateCheckItem>();
        foreach (var p in selected)
        {
            DateTime? a = null;
            var exifInvalid = false;
            if (p.DateTaken is not null && p.DateTaken.Length >= 19)
            {
                var raw = p.DateTaken[..19];
                if (raw.All(c => c is '0' or ':' or ' '))
                {
                    exifInvalid = true;
                }
                else
                {
                    try { a = DateTime.ParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); }
                    catch { exifInvalid = true; }
                }
            }

            var c = p.FileCreationTime;
            DateTime m;
            try { m = File.GetLastWriteTime(p.Path); }
            catch { m = default; }

            var cValid = c >= MinValidDate;
            var mValid = m >= MinValidDate;
            DateTime? b = null;
            if (cValid && mValid) b = c < m ? c : m;
            else if (cValid) b = c;
            else if (mValid) b = m;

            var cat = "C";
            var detail = new List<string>();
            if (!cValid && !mValid)
            {
                cat = "E";
                detail.Add("无可用文件时间");
            }
            else if (!cValid && mValid)
            {
                detail.Add("创建时间为空");
            }
            else if (cValid && !mValid)
            {
                detail.Add("修改时间为空");
            }
            else if (m < c)
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
            else if (a is not null)
            {
                cat = "C";
                detail.Add("时间正常");
            }

            if (cat == "E")
                detail.Add("需手动处理");

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

        return items;
    }

    private async void DateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information);
            return;
        }

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var analysisToken = _busyCts.Token;
        ToggleBusy(true, T("AnalyzingDates"));
        List<DateCheckItem> items;
        try
        {
            items = await Task.Run(() => BuildDateCheckItems(selected), analysisToken);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("CanceledStatus");
            ToggleBusy(false);
            return;
        }
        catch (Exception ex)
        {
            ToggleBusy(false);
            System.Windows.MessageBox.Show(this, ex.Message, T("DateCheckFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ToggleBusy(false);
        var dlg = new DateCheckDialog(items, _language == AppLanguage.English) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        var toFix = dlg.SelectedIds is null
            ? items.Where(i => (i.Category is "A" or "B" or "F") && i.FileDate != "?").ToArray()
            : items.Where(i => (i.Category is "A" or "B" or "F") && i.FileDate != "?" &&
                               dlg.SelectedIds!.Contains(i.Photo.Path, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (toFix.Length == 0)
        {
            StatusText.Text = T("NoDateFixNeeded");
            return;
        }

        var mode = GetWriteMode();
        if (mode == WriteMode.CopyToOutputDirectory && string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        {
            MessageBoxShow(T("MissingOutputMessage"), T("MissingOutputTitle"), MessageBoxImage.Warning);
            return;
        }

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = _busyCts.Token;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("DateChecking"), 0, toFix.Length));

        try
        {
            var progress = new Progress<WriteProgress>(UpdateWriteProgress);
            var batchItems = toFix.Select(i => (i.Photo, Date: $"{i.FileDate}:00")).ToArray();
            var sw = Stopwatch.StartNew();
            var batchResult = await _exifToolService.WriteDateBatchAsync(batchItems, progress, mode, OutputDirectoryTextBox.Text, token);

            if (mode == WriteMode.DirectInPlace)
            {
                UpdateWriteProgress(new WriteProgress(T("Refreshing"), null));
                await _exifToolService.ReadMetadataAsync(toFix.Select(i => i.Photo).ToArray(), token);
            }

            sw.Stop();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("DateCheckDoneStatus"), batchResult.Targets.Count) +
                              (batchResult.SkippedFileNames.Count > 0
                                  ? "  " + string.Format(CultureInfo.CurrentCulture, T("SkippedReadOnly"), batchResult.SkippedFileNames.Count)
                                  : "") +
                              $" ({sw.Elapsed.TotalSeconds:0.0}s)";
            UpdateStats();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("CanceledStatus");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    // ---- Format tools ----
    private async void FixExtensions_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var toFix = Photos.Where(p => p.IsSelected && p.IsExtensionMismatched).ToArray();
        if (toFix.Length == 0)
        {
            MessageBoxShow(T("NoMismatchSelected"), T("NoMismatchTitle"), MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            string.Format(CultureInfo.CurrentCulture, T("ConfirmFixExtensions"), toFix.Length),
            T("FixExtensions"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = _busyCts.Token;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("FixingExtensions"), toFix.Length));
        var renamed = 0;
        var skipped = 0;
        var updated = new List<PhotoItem>();

        try
        {
            foreach (var photo in toFix)
            {
                token.ThrowIfCancellationRequested();
                var correctExt = FormatDetector.GetStandardExtension(photo.DetectedFormat);
                var newPath = Path.ChangeExtension(photo.Path, correctExt.TrimStart('.'));
                if (string.Equals(photo.Path, newPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(newPath))
                {
                    skipped++;
                    photo.Status = T("ExtensionSkippedStatus");
                    continue;
                }

                try
                {
                    File.Move(photo.Path, newPath);
                    photo.UpdatePath(newPath);
                    photo.SetDetectedFormat(FormatDetector.Detect(newPath));
                    updated.Add(photo);
                    renamed++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    photo.Status = $"{T("ExtensionFailedStatus")}: {ex.Message}";
                }
            }

            if (updated.Count > 0)
            {
                try { await _exifToolService.ReadMetadataAsync(updated, token); }
                catch (OperationCanceledException) { throw; }
                  catch { /* metadata refresh is best-effort after rename */ }
            }

            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ExtensionsFixed"), renamed, skipped);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("CanceledStatus");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, T("FixExtensions"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateStats();
            ToggleBusy(false);
        }
    }

    private async void ConvertFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var selected = Photos.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBoxShow(T("NoPhotosSelectedMessage"), T("NoPhotosSelectedTitle"), MessageBoxImage.Information);
            return;
        }

        var targetFormat = ConvertFormatComboBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() switch
            {
                "png" => ImageFormat.Png,
                "bmp" => ImageFormat.Bmp,
                "gif" => ImageFormat.Gif,
                "tiff" => ImageFormat.Tiff,
                _ => ImageFormat.Jpeg
            }
            : ImageFormat.Jpeg;

        var mode = GetConvertWriteMode();
        var convertOutputDirectory = ConvertOutputTextBox.Text;
        if (mode == WriteMode.CopyToOutputDirectory && string.IsNullOrWhiteSpace(convertOutputDirectory))
        {
            MessageBoxShow(T("MissingOutputMessage"), T("MissingOutputTitle"), MessageBoxImage.Warning);
            return;
        }

        if (System.Windows.MessageBox.Show(
                this,
                string.Format(CultureInfo.CurrentCulture, T("ConfirmConvertMessage"), selected.Length, FormatDetector.GetFormatLabel(targetFormat)),
                T("ConfirmConvertTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = _busyCts.Token;
        ToggleBusy(true, string.Format(CultureInfo.CurrentCulture, T("ConvertingPhotos"), selected.Length));

        try
        {
            var sw = Stopwatch.StartNew();
            var progress = new Progress<WriteProgress>(UpdateWriteProgress);
            var results = await _imageConversionService.ConvertAsync(selected, targetFormat, mode, convertOutputDirectory, progress, token);
            sw.Stop();
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, T("ConvertDoneStatus"), results.Count) +
                              $" ({sw.Elapsed.TotalSeconds:0.0}s)";
              _thumbnailCache.Clear();
            UpdateSelectedPhotoPanel();
            UpdateStats();
            ShowToast(string.Format(CultureInfo.CurrentCulture, T("ConvertDoneMessage"), results.Count));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = T("CanceledStatus");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, T("ConvertFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ToggleBusy(false);
        }
    }

    // ---- Cancellation / busy ----
    private void CancelOperation_Click(object sender, RoutedEventArgs e) =>
        _busyCts?.Cancel();

    private void ToggleBusy(bool busy, string? status = null)
    {
        _isBusy = busy;

        foreach (var button in GetDestructiveButtons())
            if (button is not null)
                button.IsEnabled = !busy;

        WriteButton.IsEnabled = !busy && _currentCoordinate is not null;
        if (WriteProgressBar is not null)
        {
            WriteProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            WriteProgressBar.IsIndeterminate = busy;
        }
        if (CancelOperationButton is not null)
            CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null)
            StatusText.Text = status;
    }

    private IEnumerable<Button?> GetDestructiveButtons()
    {
        yield return AddPhotosButton;
        yield return AddFolderButton;
        yield return RemoveSelectedButton;
        yield return ClearButton;
        yield return FixExtensionsButton;
        yield return ConvertFormatButton;
        yield return WriteDateButton;
        yield return DateCheckButton;
        yield return DateRefButton;
        yield return BrowseOutputButton;
        yield return ConvertBrowseButton;
        yield return MapPickerButton;
        yield return ReferencePhotoButton;
    }

    private void UpdateWriteProgress(WriteProgress p)
    {
        StatusText.Text = p.Message;
        if (WriteProgressBar is null)
            return;
        if (p.Percent.HasValue)
        {
            WriteProgressBar.IsIndeterminate = false;
            WriteProgressBar.Value = Math.Clamp(p.Percent.Value, 0, 100);
        }
        else
        {
            WriteProgressBar.IsIndeterminate = true;
        }
    }

    // ---- Toast ----
    private async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;
        if (ToastBorder is null || ToastText is null)
            return;
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        try
        {
            await Task.Delay(2500, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (ToastBorder is not null)
            ToastBorder.Visibility = Visibility.Collapsed;
    }

    // ---- Panel helpers ----
    private void UpdateSelectedPhotoPanel()
    {
        var photo = GetFocusedPhoto();
        if (photo is null)
        {
            SelectedPhotoText.Text = T("NoPhotoSelected");
            SelectedGpsText.Text = "";
            PhotoThumbnail.Source = null;
            _thumbnailRequestPath = null;
            UpdateDateInfo();
            return;
        }

        SelectedPhotoText.Text = $"{photo.FileName}\n{photo.CameraDisplay ?? T("UnknownDevice")}\n{photo.DateTaken ?? T("UnknownDate")}";
        SelectedGpsText.Text = photo.Latitude.HasValue && photo.Longitude.HasValue
            ? $"GPS: {photo.GpsDisplay}"
            : T("GpsMissing");
        UseSelectedGpsButton.IsEnabled = photo.Latitude.HasValue && photo.Longitude.HasValue;

        if (!File.Exists(photo.Path))
        {
            PhotoThumbnail.Source = null;
            UpdateDateInfo();
            return;
        }

        _ = LoadThumbnailAsync(photo.Path);
        UpdateDateInfo();
    }

    private async Task LoadThumbnailAsync(string path)
    {
        _thumbnailRequestPath = path;
        if (_thumbnailCache.TryGetValue(path, out var cached))
        {
            PhotoThumbnail.Source = cached;
            return;
        }

        try
        {
            var image = await Task.Run(() => LoadThumbnailCore(path));
            if (_thumbnailRequestPath != path || image is null)
                return;

            if (_thumbnailCache.Count >= ThumbnailCacheLimit)
            {
                foreach (var key in _thumbnailCache.Keys.Take(ThumbnailCacheLimit / 2).ToArray())
                    _thumbnailCache.Remove(key);
            }
            _thumbnailCache[path] = image;
            PhotoThumbnail.Source = image;
        }
        catch
        {
            if (_thumbnailRequestPath == path)
                PhotoThumbnail.Source = null;
        }
    }

    private static BitmapImage? LoadThumbnailCore(string path)
    {
        try
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.DecodePixelWidth = 360;
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(path);
            src.EndInit();
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    private PhotoItem? GetFocusedPhoto() => PhotoGrid.SelectedItem as PhotoItem;

    private IEnumerable<PhotoItem> GetHighlightedPhotos() =>
        PhotoGrid.SelectedItems.OfType<PhotoItem>().ToArray();

    // ---- Shell navigation ----
    private void NavPhotos_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNav(NavPhotosButton);
        PhotoGrid.Focus();
        if (PhotoGrid.SelectedItem is not null)
            PhotoGrid.ScrollIntoView(PhotoGrid.SelectedItem);
    }

    private void NavLocation_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNav(NavLocationButton);
        OpenToolSection(GpsExpander);
    }

    private void NavDate_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNav(NavDateButton);
        OpenToolSection(DateToolsExpander);
    }

    private void NavFormat_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNav(NavFormatButton);
        OpenToolSection(FormatToolsExpander);
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNav(NavSettingsButton);
        ScrollToolIntoView(SettingsPanel);
    }

    private void SetActiveNav(Button active)
    {
        var inactiveStyle = (Style)FindResource("NavButton");
        var activeStyle = (Style)FindResource("NavButtonActive");
        foreach (var button in new[] { NavPhotosButton, NavLocationButton, NavDateButton, NavFormatButton, NavSettingsButton })
            button.Style = button == active ? activeStyle : inactiveStyle;
    }

    private void OpenToolSection(Expander expander)
    {
        GpsExpander.IsExpanded = ReferenceEquals(expander, GpsExpander);
        DateToolsExpander.IsExpanded = ReferenceEquals(expander, DateToolsExpander);
        FormatToolsExpander.IsExpanded = ReferenceEquals(expander, FormatToolsExpander);
        ScrollToolIntoView(expander);
    }

    private void ScrollToolIntoView(FrameworkElement element) =>
        element.Dispatcher.InvokeAsync(() => element.BringIntoView());

    private void OpenMapForPhoto(PhotoItem photo)
    {
        PhotoGrid.SelectedItem = photo;
        photo.IsSelected = true;
        var init = photo.Latitude.HasValue && photo.Longitude.HasValue
            ? new GpsCoordinate(photo.Latitude.Value, photo.Longitude.Value, photo.Altitude)
            : null;
        OpenMapEditor(init, init is null ? null : T("ExistingGpsNotice"));
    }

    // ---- Theme / language ----
    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string theme)
            App.SetTheme(theme);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _language = LanguageComboBox.SelectedItem is ComboBoxItem it && it.Tag?.ToString() == "en"
            ? AppLanguage.English
            : AppLanguage.Chinese;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        if (AppTitleText is null)
            return;

        Title = T("WindowTitle");
        AppTitleText.Text = T("AppTitle");
        AppSubtitleText.Text = T("AppSubtitle");
        ExifToolStatusText.Text = File.Exists(ExifToolService.DefaultPath) ? T("ExifToolReady") : T("ExifToolMissing");

        NavPhotosText.Text = T("NavPhotos");
        NavLocationText.Text = T("NavLocation");
        NavDateText.Text = T("NavDate");
        NavFormatText.Text = T("NavFormat");
        NavSettingsText.Text = T("NavSettings");

        AddPhotosButton.Content = T("AddPhotos");
        AddFolderButton.Content = T("AddFolder");
        IncludeSubfoldersCheckBox.Content = T("IncludeSubfolders");
        SelectAllButton.Content = T("SelectAll");
        SelectNoneButton.Content = T("SelectNone");
        MarkHighlightedButton.Content = T("MarkHighlighted");
        UnmarkHighlightedButton.Content = T("UnmarkHighlighted");
        InvertSelectionButton.Content = T("InvertSelection");
        RemoveSelectedButton.Content = T("RemoveSelected");
        ClearButton.Content = T("Clear");
        DropHintText.Text = T("DropHint");
        DropHintText2.Text = T("DropHintSub");

        WriteModeLabel.Text = T("WriteModeLabel");
        ThemeLabel.Text = T("ThemeLabel");
        LanguageLabel.Text = T("LanguageLabel");
        CopyModeItem.Content = T("CopyMode");
        DirectModeItem.Content = T("DirectMode");
        BrowseOutputButton.Content = T("Browse");
        CancelOperationButton.Content = T("CancelOperation");

        FilterAllButton.Content = T("FilterAll");
        FilterNoGpsButton.Content = T("FilterNoGps");
        FilterHasGpsButton.Content = T("FilterHasGps");
        FilterFujiButton.Content = "FUJIFILM";
        FilterHuaweiButton.Content = "HUAWEI";
        FilterMismatchButton.Content = T("FilterMismatch");

        UseColumn.Header = T("UseColumn");
        FileColumn.Header = T("FileColumn");
        DateColumn.Header = T("DateColumn");
        TimeColumn.Header = T("TimeColumn");
        DeviceColumn.Header = T("DeviceColumn");
        CoordinatesColumn.Header = T("CoordinatesColumn");
        MapColumn.Header = "Map";
        FormatColumn.Header = T("FormatColumn");
        MismatchColumn.Header = "⚠";
        PathColumn.Header = T("PathColumn");

        SelectedPhotoGroup.Text = T("SelectedPhoto");
        OpenSelectedMapButton.Content = T("OpenMap");
        UseSelectedGpsButton.Content = T("UseSelectedGps");
        GpsExpanderHeader.Text = T("ManualGpsInput");
        ManualInputHelpText.Text = T("ManualInputHelp");
        Wgs84CoordinateItem.Content = T("Wgs84Option");
        Gcj02CoordinateItem.Content = T("Gcj02Option");
        Bd09CoordinateItem.Content = T("Bd09Option");
        ReferencePhotoButton.Content = T("ReferencePhoto");
        MapPickerButton.Content = T("MapPicker");
        WriteButton.Content = T("WriteButton");

        FormatToolsGroup.Text = T("FormatTools");
        FixExtensionsButton.Content = T("FixExtensions");
        ConvertFormatTitle.Text = T("ConvertFormat");
        ConvertWriteModeLabel.Text = T("WriteModeLabel");
        ConvertCopyModeItem.Content = T("CopyMode");
        ConvertDirectModeItem.Content = T("ConvertDirectMode");
        ConvertBrowseButton.Content = T("Browse");
        ConvertFormatButton.Content = T("ConvertFormatBtn");

        DateToolsGroup.Text = T("DateTools");
        DateLabel.Text = T("DateLabel");
        WriteDateButton.Content = T("WriteDateBtn");
        DateCheckButton.Content = T("DateCheckBtn");

        WriteModeHintText.Text = GetWriteModeHint();
        if (_isGpsPlaceholderVisible)
            ShowGpsPlaceholder();
        UpdateSelectedPhotoPanel();
        UpdateStats();
        GpsInputTextBox_TextChanged(this, null);
    }

    private string T(string key) => _language == AppLanguage.Chinese ? Zh(key) : En(key);

    private static string Zh(string key) => key switch
    {
        "WindowTitle" => "Photo Info Editor",
        "AppTitle" => "照片信息编辑器",
        "AppSubtitle" => "照片元数据编辑工具：GPS 位置、拍摄时间、格式检测与转换。",
        "ExifToolReady" => "ExifTool ready",
        "ExifToolMissing" => "ExifTool not found",
        "NavPhotos" => "照片",
        "NavLocation" => "位置",
        "NavDate" => "日期",
        "NavFormat" => "格式",
        "NavSettings" => "设置",
        "AddPhotos" => "添加照片",
        "AddFolder" => "添加文件夹",
        "IncludeSubfolders" => "包含子文件夹",
        "SelectAll" => "全选",
        "SelectNone" => "全不选",
        "MarkHighlighted" => "选中高亮",
        "UnmarkHighlighted" => "取消高亮",
        "InvertSelection" => "反选",
        "RemoveSelected" => "移除选中",
        "Clear" => "清空",
        "DropHint" => "拖拽照片或文件夹到窗口",
        "DropHintSub" => "支持拖入照片、文件夹；批量写入 GPS / 拍摄时间，或转换格式。",
        "WriteModeLabel" => "写入方式",
        "ThemeLabel" => "主题",
        "LanguageLabel" => "语言",
        "CopyMode" => "输出到新目录",
        "DirectMode" => "直接写入原文件",
        "ConvertDirectMode" => "直接替换原文件",
        "Browse" => "浏览",
        "CancelOperation" => "取消",
        "FolderDialogTitle" => "选择照片文件夹",
        "OutputDialogTitle" => "选择输出目录",
        "ImportReportTitle" => "导入报告",
        "ImportReportPartial" => "已导入 {0} 张照片，跳过 {1} 张不支持的格式（{2}）。",
        "ImportReportNone" => "文件夹中 {0} 个文件均不支持（{1}）。",
        "NoNewPhotos" => "没有新增支持的照片。",
        "ReadingMetadata" => "正在读取 {0} 张照片的 metadata...",
        "ImportedPhotos" => "已导入 {0} 张照片。",
        "ReadFailedTitle" => "读取失败",
        "ReadFailedStatus" => "读取失败",
        "FilterAll" => "全部",
        "FilterNoGps" => "无 GPS",
        "FilterHasGps" => "有 GPS",
        "FilterMismatch" => "格式⚠",
        "DateColumn" => "日期",
        "TimeColumn" => "时间",
        "FormatColumn" => "格式",
        "UseColumn" => "使用",
        "FileColumn" => "文件",
        "DeviceColumn" => "设备",
        "CoordinatesColumn" => "坐标",
        "PathColumn" => "路径",
        "SelectedPhoto" => "图片信息",
        "OpenMap" => "Map",
        "UseSelectedGps" => "填入 GPS 输入框",
        "ManualGpsInput" => "GPS 编辑",
        "ManualInputHelp" => "支持十进制度、N/E 前后缀、DMS；海拔可写为 alt=海拔。",
        "GpsPlaceholder" => "输入 GPS 坐标，例如：纬度, 经度 或 N纬度 E经度；可选 alt=海拔",
        "Wgs84Option" => "WGS-84 - GPS/EXIF/Google Earth",
        "Gcj02Option" => "GCJ-02 - 高德/腾讯/华为地图",
        "Bd09Option" => "BD-09 - 百度地图",
        "CoordinateSystemHelpTitle" => "坐标系怎么选",
        "CoordinateSystemHelp" => "写入 EXIF 时程序会统一转换为 WGS-84。\n\n选 WGS-84：相机 GPS、手机原始 GPS、EXIF、Google Earth、OpenStreetMap。\n\n选 GCJ-02：来自高德、腾讯、华为地图、国内 Apple 地图等坐标。\n\n选 BD-09：来自百度地图的坐标。",
        "ReferencePhoto" => "参考照片",
        "MapPicker" => "Map 选点",
        "WriteButton" => "写入 GPS 到选中照片",
        "CopyModeHint" => "会先复制照片到输出目录，只修改副本。",
        "DirectModeHint" => "直接修改原文件，不创建备份；建议先自行备份。",
        "ExistingGpsNotice" => "此照片已有 GPS 信息。确认后会作为新的待写入坐标，写入时覆盖原 GPS。",
        "OverwriteGpsNotice" => "注意：选中照片中有 {0} 张已有 GPS，继续写入会覆盖它们的原 GPS。",
        "ReferenceNoGpsMessage" => "参考照片没有 GPS 信息。",
        "ReferenceLoadedStatus" => "已从参考照片 {0} 读取 GPS。",
        "WillWrite" => "将写入：{0} ({1}, {2})",
        "MapPickedStatus" => "已从 Map 选点填入 GPS。",
        "NoPhotosSelectedMessage" => "请先选择至少一张照片。",
        "NoPhotosSelectedTitle" => "没有选中照片",
        "InvalidCoordinateMessage" => "请输入有效 GPS 坐标。",
        "InvalidCoordinateTitle" => "坐标无效",
        "MissingOutputMessage" => "请选择输出目录。",
        "MissingOutputTitle" => "缺少输出目录",
        "CopyToConfirm" => "输出到：{0}",
        "DirectConfirm" => "直接写入原文件，不创建备份",
        "ConfirmWriteMessage" => "Photos: {0}\nGPS: {1}\n{2}",
        "ConfirmWriteTitle" => "确认写入",
        "WritingPhotos" => "正在写入 {0} 张照片...",
        "WriteDoneStatus" => "完成，已写入 {0} 张照片。",
        "WriteDoneMessage" => "GPS 写入完成。",
        "WriteFailedTitle" => "写入失败",
        "WrittenStatus" => "已写入",
        "CanceledStatus" => "操作已取消。",
        "Refreshing" => "正在刷新 metadata...",
        "SelectedNoGpsStatus" => "选中照片没有 GPS。",
        "NoMismatch" => "当前没有异常后缀",
        "NoMismatchSelected" => "请先选中格式不匹配的照片（筛选→格式⚠，然后勾选需要更正的）。",
        "NoMismatchTitle" => "未选中不匹配文件",
        "ConfirmFixExtensions" => "确定要按真实格式更正 {0} 个文件的后缀名吗？此操作会重命名原文件。",
        "FixExtensions" => "更正后缀",
        "FixingExtensions" => "正在更正后缀...",
        "ExtensionsFixed" => "已更正 {0} 个，跳过 {1} 个。",
        "ExtensionSkippedStatus" => "目标文件已存在，跳过",
        "ExtensionFailedStatus" => "重命名失败",
        "FormatTools" => "格式工具",
        "ConvertFormat" => "目标格式",
        "ConvertFormatBtn" => "转换格式",
        "ConfirmConvertMessage" => "转换 {0} 张照片为 {1} 格式？\n注意：仅选中照片会转换。",
        "ConfirmConvertTitle" => "确认转换格式",
        "ConvertingPhotos" => "正在转换 {0} 张照片格式...",
        "ConvertDoneStatus" => "完成，已转换 {0} 张照片。",
        "ConvertDoneMessage" => "格式转换完成 ({0} 张)。",
        "ConvertFailedTitle" => "转换失败",
        "DateTools" => "日期工具",
        "DateLabel" => "拍摄日期",
        "WriteDateBtn" => "写入日期",
        "DateCheckBtn" => "日期校对",
        "DateInfoExif" => "拍摄时间",
        "DateInfoFileCreated" => "文件创建",
        "DateNotRead" => "未读取",
        "InvalidDateMessage" => "请先选择拍摄日期。",
        "InvalidTimeMessage" => "时间格式无效，请输入 HH:MM（例如 14:30）。",
        "InvalidDateTitle" => "日期无效",
        "ConfirmWriteDateMessage" => "将 {0} 张照片的拍摄时间写入为:\n{1}\n\n{2}\n\n确定？",
        "ConfirmWriteDateTitle" => "写入日期",
        "WritingDates" => "正在写入日期到 {0} 张照片...",
        "WriteDateDoneStatus" => "已写入日期到 {0} 张照片。",
        "WriteDateFailedTitle" => "写入日期失败",
        "AnalyzingDates" => "正在分析 EXIF 与文件时间...",
        "DateChecking" => "日期校对中 {0}/{1}...",
        "DateCheckDoneStatus" => "日期校对完成，已写入 {0} 张。",
        "DateCheckFailedTitle" => "日期校对失败",
        "NoDateFixNeeded" => "无需修改。",
        "SkippedReadOnly" => "（跳过 {0} 个只读格式）",
        "StatsTotal" => "共 {0} 张",
        "StatsHasGps" => "{0} 张有 GPS",
        "StatsSelected" => "选中 {0} 张",
        "NoPhotoSelected" => "未选择照片。",
        "UnknownDevice" => "未知设备",
        "UnknownDate" => "未知日期",
        "GpsMissing" => "GPS: 缺失",
        "ConfirmRemoveSelected" => "确定从列表移除选中的 {0} 张照片吗？不会删除磁盘文件。",
        "ConfirmRemoveTitle" => "移除选中",
        "ConfirmClear" => "确定清空当前列表吗？不会删除磁盘文件。",
        "ConfirmClearTitle" => "清空列表",
        _ => key
    };

    private static string En(string key) => key switch
    {
        "WindowTitle" => "Photo Info Editor",
        "AppTitle" => "Photo Info Editor",
        "AppSubtitle" => "Photo metadata editor: GPS, capture time, format detection and conversion.",
        "ExifToolReady" => "ExifTool ready",
        "ExifToolMissing" => "ExifTool not found",
        "NavPhotos" => "Photos",
        "NavLocation" => "Location",
        "NavDate" => "Date",
        "NavFormat" => "Format",
        "NavSettings" => "Settings",
        "AddPhotos" => "Add Photos",
        "AddFolder" => "Add Folder",
        "IncludeSubfolders" => "Include subfolders",
        "SelectAll" => "Select All",
        "SelectNone" => "Select None",
        "MarkHighlighted" => "Select Rows",
        "UnmarkHighlighted" => "Unselect Rows",
        "InvertSelection" => "Invert",
        "RemoveSelected" => "Remove",
        "Clear" => "Clear",
        "DropHint" => "Drop photos or folders here",
        "DropHintSub" => "Drag in photos or folders to batch-write GPS / capture time, or convert formats.",
        "WriteModeLabel" => "Write mode",
        "ThemeLabel" => "Theme",
        "LanguageLabel" => "Language",
        "CopyMode" => "Copy to new folder",
        "DirectMode" => "Write originals",
        "ConvertDirectMode" => "Replace originals",
        "Browse" => "Browse",
        "CancelOperation" => "Cancel",
        "FolderDialogTitle" => "Choose a photo folder",
        "OutputDialogTitle" => "Choose output directory",
        "ImportReportTitle" => "Import report",
        "ImportReportPartial" => "Imported {0} photos, skipped {1} unsupported files ({2}).",
        "ImportReportNone" => "All {0} files in the folder are unsupported ({1}).",
        "NoNewPhotos" => "No new supported photos were added.",
        "ReadingMetadata" => "Reading metadata for {0} photos...",
        "ImportedPhotos" => "Imported {0} photos.",
        "ReadFailedTitle" => "Read failed",
        "ReadFailedStatus" => "Read failed",
        "FilterAll" => "All",
        "FilterNoGps" => "No GPS",
        "FilterHasGps" => "Has GPS",
        "FilterMismatch" => "Format⚠",
        "DateColumn" => "Date",
        "TimeColumn" => "Time",
        "FormatColumn" => "Format",
        "UseColumn" => "Use",
        "FileColumn" => "File",
        "DeviceColumn" => "Device",
        "CoordinatesColumn" => "Coordinates",
        "PathColumn" => "Path",
        "SelectedPhoto" => "Photo Info",
        "OpenMap" => "Map",
        "UseSelectedGps" => "Use as GPS input",
        "ManualGpsInput" => "GPS Editing",
        "ManualInputHelp" => "Supports decimal degrees, N/E prefixes or suffixes, DMS, and optional altitude like alt=altitude.",
        "GpsPlaceholder" => "Enter GPS coordinates, e.g. latitude, longitude or Nlatitude Elongitude; optional alt=altitude",
        "Wgs84Option" => "WGS-84 - GPS/EXIF/Google Earth",
        "Gcj02Option" => "GCJ-02 - AMap/Tencent/Huawei Maps",
        "Bd09Option" => "BD-09 - Baidu Map",
        "CoordinateSystemHelpTitle" => "Which coordinate system?",
        "CoordinateSystemHelp" => "The app always writes EXIF as WGS-84.\n\nWGS-84: camera GPS, raw phone GPS, EXIF, Google Earth, OpenStreetMap.\n\nGCJ-02: coordinates from AMap, Tencent Maps, Huawei Maps and mainland China Apple Maps.\n\nBD-09: Baidu Map coordinates.",
        "ReferencePhoto" => "Reference Photo",
        "MapPicker" => "Map Picker",
        "WriteButton" => "Write GPS To Selected Photos",
        "CopyModeHint" => "Photos are copied to the output directory first. Only the copies are modified.",
        "DirectModeHint" => "Original files are modified directly. No backup is created; make your own backup first.",
        "ExistingGpsNotice" => "This photo already has GPS. Confirming fills a new pending coordinate; writing will overwrite the old GPS.",
        "OverwriteGpsNotice" => "Note: {0} selected photos already have GPS data. Writing will overwrite them.",
        "ReferenceNoGpsMessage" => "The reference photo has no GPS data.",
        "ReferenceLoadedStatus" => "Loaded GPS from reference photo {0}.",
        "WillWrite" => "Will write: {0} ({1}, {2})",
        "MapPickedStatus" => "Filled GPS from Map Picker.",
        "NoPhotosSelectedMessage" => "Select at least one photo first.",
        "NoPhotosSelectedTitle" => "No photos selected",
        "InvalidCoordinateMessage" => "Enter a valid GPS coordinate.",
        "InvalidCoordinateTitle" => "Invalid coordinate",
        "MissingOutputMessage" => "Choose an output directory.",
        "MissingOutputTitle" => "Missing output directory",
        "CopyToConfirm" => "Copy to: {0}",
        "DirectConfirm" => "Write directly to original files without backup",
        "ConfirmWriteMessage" => "Photos: {0}\nGPS: {1}\n{2}",
        "ConfirmWriteTitle" => "Confirm write",
        "WritingPhotos" => "Writing {0} photos...",
        "WriteDoneStatus" => "Done. Wrote GPS to {0} photos.",
        "WriteDoneMessage" => "GPS write complete.",
        "WriteFailedTitle" => "Write failed",
        "WrittenStatus" => "Written",
        "CanceledStatus" => "Operation canceled.",
        "Refreshing" => "Refreshing metadata...",
        "SelectedNoGpsStatus" => "Selected photo has no GPS data.",
        "NoMismatch" => "No mismatched extensions",
        "NoMismatchSelected" => "Select mismatched photos first (filter by Format⚠, then check the ones to fix).",
        "NoMismatchTitle" => "No mismatched files selected",
        "ConfirmFixExtensions" => "Rename {0} files to their detected format extension? This renames the original files.",
        "FixExtensions" => "Fix Extensions",
        "FixingExtensions" => "Fixing extensions...",
        "ExtensionsFixed" => "Renamed {0} files, skipped {1}.",
        "ExtensionSkippedStatus" => "Target exists, skipped",
        "ExtensionFailedStatus" => "Rename failed",
        "FormatTools" => "Format Tools",
        "ConvertFormat" => "Target Format",
        "ConvertFormatBtn" => "Convert",
        "ConfirmConvertMessage" => "Convert {0} photos to {1}?\nOnly selected photos will be converted.",
        "ConfirmConvertTitle" => "Confirm convert",
        "ConvertingPhotos" => "Converting {0} photos...",
        "ConvertDoneStatus" => "Done. Converted {0} photos.",
        "ConvertDoneMessage" => "Format conversion done ({0} photos).",
        "ConvertFailedTitle" => "Convert failed",
        "DateTools" => "Date Tools",
        "DateLabel" => "Capture Date",
        "WriteDateBtn" => "Write Date",
        "DateCheckBtn" => "Date Check",
        "DateInfoExif" => "Capture time",
        "DateInfoFileCreated" => "File created",
        "DateNotRead" => "not read",
        "InvalidDateMessage" => "Select a capture date first.",
        "InvalidTimeMessage" => "Invalid time. Use HH:MM (e.g. 14:30).",
        "InvalidDateTitle" => "Invalid date",
        "ConfirmWriteDateMessage" => "Write capture time to {0} photos as:\n{1}\n\n{2}\n\nContinue?",
        "ConfirmWriteDateTitle" => "Write date",
        "WritingDates" => "Writing capture time to {0} photos...",
        "WriteDateDoneStatus" => "Wrote capture time to {0} photos.",
        "WriteDateFailedTitle" => "Date write failed",
        "AnalyzingDates" => "Analyzing EXIF and file times...",
        "DateChecking" => "Date check {0}/{1}...",
        "DateCheckDoneStatus" => "Date check complete, wrote {0} photos.",
        "DateCheckFailedTitle" => "Date check failed",
        "NoDateFixNeeded" => "Nothing to fix.",
        "SkippedReadOnly" => "(skipped {0} read-only files)",
        "StatsTotal" => "Total: {0}",
        "StatsHasGps" => "GPS: {0}",
        "StatsSelected" => "Selected: {0}",
        "NoPhotoSelected" => "No photo selected.",
        "UnknownDevice" => "Unknown device",
        "UnknownDate" => "Unknown date",
        "GpsMissing" => "GPS: missing",
        "ConfirmRemoveSelected" => "Remove {0} selected photos from the list? Disk files are not deleted.",
        "ConfirmRemoveTitle" => "Remove selected",
        "ConfirmClear" => "Clear the current list? Disk files are not deleted.",
        "ConfirmClearTitle" => "Clear list",
        _ => key
    };

    private enum AppLanguage { Chinese, English }
}
