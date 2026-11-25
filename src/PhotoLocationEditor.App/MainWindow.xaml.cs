using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Web;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using PhotoLocationEditor.App.Models;
using PhotoLocationEditor.App.Services;
using DragEventArgs = System.Windows.DragEventArgs;

namespace PhotoLocationEditor.App;

public partial class MainWindow : Window
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    private readonly AppSettingsService _settingsService = new();
    private readonly ExifToolService _exifToolService = new(ExifToolService.DefaultPath);
    private readonly ThumbnailService _thumbnailService;
    private readonly AppSettings _settings;
    private WebBridgeHost? _bridge;

    public MainWindow()
    {
        _thumbnailService = new ThumbnailService(_exifToolService);
        _settings = _settingsService.Load();

        InitializeComponent();
        ApplyHostBackground(_settings.Theme);
        RestoreWindowState();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDwmAttributes(_settings.Theme != "sakura");

        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoInfoEditor",
                "WebView2Data");
            Directory.CreateDirectory(userDataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await StudioWebView.EnsureCoreWebView2Async(env);

            var core = StudioWebView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            var webUiDir = Path.Combine(AppContext.BaseDirectory, "WebUI");
            if (!Directory.Exists(webUiDir))
                throw new DirectoryNotFoundException($"WebUI folder not found: {webUiDir}");

            core.SetVirtualHostNameToFolderMapping(
                "app.photoinfo.local",
                webUiDir,
                CoreWebView2HostResourceAccessKind.Allow);

            core.AddWebResourceRequestedFilter(
                "https://media.photoinfo.local/*",
                CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += CoreWebView2_WebResourceRequested;

            _bridge = new WebBridgeHost(
                this,
                core,
                _exifToolService,
                _thumbnailService,
                _settingsService,
                _settings,
                theme => Dispatcher.InvokeAsync(() =>
                {
                    ApplyHostBackground(theme);
                    ApplyDwmAttributes(theme != "sakura");
                }));

            core.WebMessageReceived += CoreWebView2_WebMessageReceived;
            core.NavigationCompleted += (_, _) =>
            {
                StudioWebView.Visibility = Visibility.Visible;
                SplashOverlay.Visibility = Visibility.Collapsed;
            };

            core.Navigate("https://app.photoinfo.local/index.html");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"WebView2 initialization failed. Please ensure Microsoft Edge WebView2 Runtime is installed.\n\n{ex.Message}",
                "Photo Info Editor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_bridge is null)
            return;

        try
        {
            await _bridge.HandleMessageAsync(e.WebMessageAsJson, e.AdditionalObjects);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bridge message error: {ex.Message}");
        }
    }

    private async void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var id = query["id"] ?? "";
            var photo = _bridge?.FindPhotoById(id);

            if (photo is null || !File.Exists(photo.Path))
            {
                e.Response = StudioWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null, 404, "Not Found", "Cache-Control: no-store");
                return;
            }

            if (uri.AbsolutePath.Equals("/preview", StringComparison.OrdinalIgnoreCase))
            {
                var fmt = photo.DetectedFormat != ImageFormat.Unknown
                    ? photo.DetectedFormat
                    : FormatDetector.Detect(photo.Path);

                // Stream native web-decodable images directly without recompression
                if (fmt is ImageFormat.Jpeg or ImageFormat.Png or ImageFormat.WebP or ImageFormat.Gif or ImageFormat.Avif)
                {
                    var bytes = await File.ReadAllBytesAsync(photo.Path);
                    var mime = fmt switch
                    {
                        ImageFormat.Png => "image/png",
                        ImageFormat.WebP => "image/webp",
                        ImageFormat.Gif => "image/gif",
                        ImageFormat.Avif => "image/avif",
                        _ => "image/jpeg"
                    };
                    e.Response = StudioWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(bytes), 200, "OK", $"Content-Type: {mime}\r\nCache-Control: private, max-age=120");
                    return;
                }

                // HEIC, RAW, TIFF, Video -> high-res JPEG preview via Shell / ExifTool / WIC
                var previewBytes = await _thumbnailService.GetThumbnailBytesAsync(photo.Path, 1920);
                if (previewBytes is { Length: > 0 })
                {
                    e.Response = StudioWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(previewBytes), 200, "OK", "Content-Type: image/jpeg\r\nCache-Control: private, max-age=120");
                    return;
                }
            }
            else
            {
                var w = int.TryParse(query["w"], out var parsedW) ? parsedW : 380;
                var thumbBytes = await _thumbnailService.GetThumbnailBytesAsync(photo.Path, w);
                if (thumbBytes is { Length: > 0 })
                {
                    e.Response = StudioWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(thumbBytes), 200, "OK", "Content-Type: image/jpeg\r\nCache-Control: private, max-age=300");
                    return;
                }
            }

            e.Response = StudioWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null, 404, "Not Found", "Cache-Control: no-store");
        }
        catch
        {
            try
            {
                e.Response = StudioWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null, 500, "Error", "Cache-Control: no-store");
            }
            catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_bridge is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
            return;

        var expanded = new List<string>();
        foreach (var path in dropped)
        {
            if (File.Exists(path) && ExifToolService.IsSupportedImage(path))
                expanded.Add(path);
            else if (Directory.Exists(path))
                expanded.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(ExifToolService.IsSupportedImage));
        }
        if (expanded.Count > 0)
            await _bridge.AddPathsAsync(expanded);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);
        _bridge?.EmitWindowState();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowStateValue = (int)WindowState;
            _settingsService.Save(_settings);
        }
        catch { }

        if (_bridge is not null)
        {
            try { await _bridge.DisposeAsync(); }
            catch { }
        }
    }

    private void ApplyHostBackground(string theme)
    {
        var hex = theme switch
        {
            "bleu" => "#091629",
            "aurora" => "#0B0718",
            "amber" => "#160B06",
            "dune" => "#19130F",
            "sakura" => "#F6F3EC",
            _ => "#090B10"
        };
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Background = brush;
        RootGrid.Background = brush;
        SplashOverlay.Background = brush;
        StudioWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private void ApplyDwmAttributes(bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == nint.Zero)
                return;
            var darkVal = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref darkVal, sizeof(int));
            var cornerVal = DwmCornerRound;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref cornerVal, sizeof(int));
        }
        catch { }
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
                    Left = candidate.Left;
                    Top = candidate.Top;
                    Width = candidate.Width;
                    Height = candidate.Height;
                }
            }
            if (_settings.WindowStateValue == (int)WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private static bool IsVisibleOnAnyScreen(Rect r) =>
        r.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        r.Right > SystemParameters.VirtualScreenLeft &&
        r.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight &&
        r.Bottom > SystemParameters.VirtualScreenTop;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
}
