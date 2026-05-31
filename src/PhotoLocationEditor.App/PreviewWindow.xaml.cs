using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PhotoLocationEditor.App.Models;

namespace PhotoLocationEditor.App;

public partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<PhotoItem> _photos;
    private int _index;

    public PreviewWindow(IReadOnlyList<PhotoItem> photos, int startIndex)
    {
        _photos = photos;
        _index = startIndex;
        InitializeComponent();
        LoadImage();
    }

    private void LoadImage()
    {
        var photo = _photos[_index];
        Title = photo.FileName;
        InfoText.Text = $"{photo.FileName}  {photo.CameraDisplay}  ({_index + 1}/{_photos.Count})";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _photos.Count - 1;

        try
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(photo.Path);
            src.EndInit();
            FullImage.Source = src;
        }
        catch
        {
            InfoText.Text += " (无法加载)";
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Up or Key.PageUp)
        {
            Navigate(-1);
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.Down or Key.PageDown)
        {
            Navigate(1);
            e.Handled = true;
        }
    }

    private void Navigate(int delta)
    {
        var newIndex = _index + delta;
        if (newIndex < 0 || newIndex >= _photos.Count) return;
        _index = newIndex;
        LoadImage();

        // sync table highlight in MainWindow
        var photo = _photos[_index];
        if (Owner is MainWindow main)
        {
            for (var i = 0; i < main.Photos.Count; i++)
            {
                if (main.Photos[i] == photo)
                {
                    main.PhotoGrid.SelectedIndex = i;
                    main.PhotoGrid.ScrollIntoView(main.Photos[i]);
                    main.Focus(); // reset focus so next keyboard nav works on table too
                    break;
                }
            }
        }
    }
}
