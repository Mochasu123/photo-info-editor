using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;

namespace PhotoLocationEditor.App;

public partial class ReferencePhotoDialog : Window
{
    private readonly ExifToolService _exifTool;
    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService = new();
    private TabItem? _draggedTab;

    public ReferencePhotoDialog(IReadOnlyList<PhotoItem> importedPhotos, ExifToolService exifTool, AppSettings settings)
    {
        _exifTool = exifTool;
        _settings = settings;
        InitializeComponent();

        // Restore tab order
        if (_settings.ReferencePhotoTabOrder.Count == 2)
        {
            var a0 = MainTabControl.Items[0] as TabItem;
            var a1 = MainTabControl.Items[1] as TabItem;
            if (a0 is not null && a1 is not null)
            {
                MainTabControl.Items.Clear();
                MainTabControl.Items.Add(_settings.ReferencePhotoTabOrder[0] == 0 ? a0 : a1);
                MainTabControl.Items.Add(_settings.ReferencePhotoTabOrder[0] == 0 ? a1 : a0);
            }
        }
        MainTabControl.SelectedIndex = _settings.ReferencePhotoTabIndex;

        var hasGps = importedPhotos.Where(p => p.Latitude.HasValue && p.Longitude.HasValue).ToArray();
        if (hasGps.Length == 0)
        {
            EmptyHintText.Visibility = Visibility.Visible;
            UseImportedButton.IsEnabled = false;
        }
        else
        {
            foreach (var p in hasGps) ImportedListBox.Items.Add(p);
            ImportedListBox.SelectedIndex = 0;
        }

        ApplyLanguage();
    }

    public GpsCoordinate? Result { get; private set; }
    public string? ResultFileName { get; private set; }

    private string Tk(string key) => _settings.LastLanguage == "en" ? En(key) : Zh(key);

    private void ApplyLanguage()
    {
        Title = Tk("dialogTitle");
        DialogTitle.Text = Tk("dialogTitle");
        TabHelpText.Text = Tk("tabHelp");
        LocalFileTabText.Text = Tk("localFileTab");
        ImportedTabText.Text = Tk("importedTab");
        ChooseFileBtn.Content = Tk("chooseFile");
        UseFileButton.Content = Tk("useFile");
        FileInfoText.Text = Tk("noFile");
        UseImportedButton.Content = Tk("useImported");
        CancelBtn.Content = Tk("cancel");
        EmptyHintText.Text = Tk("noGpsImported");
    }

    // ---- Tab drag-reorder ----

    private void LocalFileTab_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            StartTabDrag(LocalFileTab);
    }

    private void ImportedTab_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            StartTabDrag(ImportedTab);
    }

    private void StartTabDrag(TabItem tab)
    {
        _draggedTab = tab;
        DragDrop.DoDragDrop(tab, tab, System.Windows.DragDropEffects.Move);
    }

    private void TabControl_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (_draggedTab is null) return;
        var pos = e.GetPosition(MainTabControl);
        foreach (TabItem item in MainTabControl.Items)
        {
            if (item == _draggedTab) continue;
            var headerPos = item.TranslatePoint(new(0, 0), MainTabControl);
            if (pos.X > 0 && pos.X < MainTabControl.ActualWidth &&
                pos.Y >= headerPos.Y && pos.Y < headerPos.Y + item.ActualHeight)
            {
                e.Effects = System.Windows.DragDropEffects.Move;
                return;
            }
        }
        e.Effects = System.Windows.DragDropEffects.None;
    }

    private void TabControl_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_draggedTab is null) return;
        var pos = e.GetPosition(MainTabControl);
        var targetIndex = 0;
        foreach (TabItem item in MainTabControl.Items)
        {
            if (item == _draggedTab) continue;
            var headerPos = item.TranslatePoint(new(0, 0), MainTabControl);
            if (pos.Y >= headerPos.Y + item.ActualHeight / 2) targetIndex++;
            else break;
        }
        var sourceIndex = MainTabControl.Items.IndexOf(_draggedTab);
        MainTabControl.Items.Remove(_draggedTab);
        if (targetIndex > sourceIndex) targetIndex--;
        MainTabControl.Items.Insert(targetIndex, _draggedTab);
        MainTabControl.SelectedItem = _draggedTab;
        _draggedTab = null;
        SaveTabOrder();
    }

    private void TabItem_Drop(object sender, System.Windows.DragEventArgs e) { }

    private void SaveTabOrder()
    {
        var order = new List<int>();
        foreach (TabItem item in MainTabControl.Items)
            order.Add(item == LocalFileTab ? 0 : 1);
        _settings.ReferencePhotoTabOrder = order;
        _settingsService.Save(_settings);
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.ReferencePhotoTabIndex = MainTabControl.SelectedIndex;
        _settingsService.Save(_settings);
    }

    // ---- Imported tab ----

    private void UseImported_Click(object sender, RoutedEventArgs e)
    {
        if (ImportedListBox.SelectedItem is PhotoItem photo && photo.Latitude.HasValue && photo.Longitude.HasValue)
        {
            Result = new GpsCoordinate(photo.Latitude.Value, photo.Longitude.Value, photo.Altitude);
            ResultFileName = photo.FileName;
            DialogResult = true;
            Close();
        }
    }

    // ---- Local file tab ----

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = false,
            Filter = "Image/Video files|*.jpg;*.jpeg;*.heic;*.heif;*.hif;*.png;*.webp|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        var reference = new PhotoItem(dialog.FileName);
        try { await _exifTool.ReadMetadataAsync(new[] { reference }); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Read failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!reference.Latitude.HasValue || !reference.Longitude.HasValue)
        {
            FileInfoText.Text = dialog.FileName;
            FileGpsText.Text = Tk("noGpsFound");
            UseFileButton.IsEnabled = false;
            return;
        }

        FileInfoText.Text = dialog.FileName;
        FileGpsText.Text = $"GPS: {reference.GpsDisplay}";
        UseFileButton.IsEnabled = true;
        UseFileButton.Tag = reference;
    }

    private void UseFile_Click(object sender, RoutedEventArgs e)
    {
        if (UseFileButton.Tag is PhotoItem photo && photo.Latitude.HasValue && photo.Longitude.HasValue)
        {
            Result = new GpsCoordinate(photo.Latitude.Value, photo.Longitude.Value, photo.Altitude);
            ResultFileName = photo.FileName;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string Zh(string key) => key switch
    {
        "dialogTitle" => "选择参考照片",
        "tabHelp" => "按住 Tab 标题拖拽可调整前后顺序",
        "localFileTab" => "从本地文件选取",
        "importedTab" => "从已导入选取",
        "chooseFile" => "选择文件...",
        "useFile" => "使用此文件",
        "noFile" => "未选择文件",
        "useImported" => "使用选中照片",
        "cancel" => "取消",
        "noGpsFound" => "该照片没有 GPS 信息。",
        "noGpsImported" => "目前已导入照片均无 GPS 信息。",
        _ => key
    };

    private static string En(string key) => key switch
    {
        "dialogTitle" => "Select Reference Photo",
        "tabHelp" => "Drag tab headers to reorder",
        "localFileTab" => "From Local File",
        "importedTab" => "From Imported",
        "chooseFile" => "Choose File...",
        "useFile" => "Use This File",
        "noFile" => "No file selected",
        "useImported" => "Use Selected Photo",
        "cancel" => "Cancel",
        "noGpsFound" => "This photo has no GPS data.",
        "noGpsImported" => "No imported photos have GPS data.",
        _ => key
    };
}
