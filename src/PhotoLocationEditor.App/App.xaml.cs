using System.IO;
using System.Windows;
using PhotoLocationEditor.App.Services;

namespace PhotoLocationEditor.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = await RunSmokeTestAsync();
            Shutdown(exitCode);
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private static async Task<int> RunSmokeTestAsync()
    {
        try
        {
            var webUi = Path.Combine(AppContext.BaseDirectory, "WebUI", "index.html");
            if (!File.Exists(webUi))
                return 2;
            if (!File.Exists(ExifToolService.DefaultPath))
                return 3;

            await using var exifTool = new ExifToolService(ExifToolService.DefaultPath);
            await exifTool.WarmUpAsync();
            return exifTool.IsDaemonRunning ? 0 : 4;
        }
        catch
        {
            return 5;
        }
    }
}
