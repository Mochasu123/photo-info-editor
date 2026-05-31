using System.Windows;

namespace PhotoLocationEditor.App;

public partial class App : System.Windows.Application
{
    public static readonly string[] ThemeNames = ["light", "sepia", "dark"];
    public static readonly string[] ThemeLabels = ["晨光 Light", "薄暮 Sepia", "暗夜 Dark"];

    public static void SetTheme(string name)
    {
        var merged = ((App)System.Windows.Application.Current).Resources.MergedDictionaries;
        var index = name switch { "sepia" => 1, "dark" => 2, _ => 0 };
        var uri = new System.Uri($"Themes/{ThemeNames[index]}.xaml", System.UriKind.Relative);

        if (merged.Count > 0 && merged[0].Source?.OriginalString == uri.OriginalString)
            return; // already set

        var dict = new ResourceDictionary { Source = uri };
        merged[0] = dict; // replace color theme, keep Shared.xaml at index 1
    }
}
