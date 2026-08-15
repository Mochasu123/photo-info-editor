using System.Windows;
using System.Windows.Controls;

namespace PhotoLocationEditor.App;

public partial class DateCheckDialog : Window
{
    private readonly List<DateCheckItem> _items;
    private readonly bool _isEnglish;

    public DateCheckDialog(List<DateCheckItem> items, bool isEnglish = false)
    {
        _items = items;
        _isEnglish = isEnglish;
        InitializeComponent();
        Title = _isEnglish ? "Date Check" : "日期校对";

        var suggestFix = items.Where(i => i.Category is "A" or "B" or "F").ToArray();  // actionable
          var suggestAB = items.Where(i => i.Category is "A" or "B").ToArray();      // missing/late EXIF
        var ok = items.Where(i => i.Category == "C").ToArray();                          // fine
        var noTime = items.Where(i => i.Category == "E").ToArray();                      // unfixable

        SummaryText.Text = _isEnglish
              ? $"Analyzed {items.Count}: fix {suggestFix.Length} | ok {ok.Length} | no usable time {noTime.Length}"
              : $"共分析 {items.Count} 张：建议处理 {suggestFix.Length} 张 | 无需处理 {ok.Length} 张 | 无可用时间 {noTime.Length} 张";

        if (suggestFix.Length > 0) AddCategory(_isEnglish ? "Suggested fixes — corrected time will be written" : "建议处理 — 将写入校准后的时间", suggestFix, System.Windows.Media.Brushes.DarkOrange);
        if (ok.Length > 0) AddCategory(_isEnglish ? "No action — time is consistent" : "无需处理 — 时间已正常", ok, System.Windows.Media.Brushes.ForestGreen);
        if (noTime.Length > 0) AddCategory(_isEnglish ? "No usable file time — write manually" : "无可用时间 — 文件时间缺失或全为零，需手动写入", noTime, System.Windows.Media.Brushes.Purple);

        ExecuteABBtn.IsEnabled = suggestAB.Length > 0;
        ExecuteABBtn.Content = _isEnglish ? "Fix A+B only" : "仅处理 A+B";
        ExecuteAllBtn.Content = _isEnglish ? "Fix all suggested" : "全部处理";
        CancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        ExecuteABBtn.Click += (_, _) => { SelectedIds = suggestAB.Select(i => i.Photo.Path).ToHashSet(StringComparer.OrdinalIgnoreCase); DialogResult = true; Close(); };
        ExecuteAllBtn.Click += (_, _) => { SelectedIds = null; DialogResult = true; Close(); };
    }

    public HashSet<string>? SelectedIds { get; private set; }

    private void AddCategory(string title, DateCheckItem[] items, System.Windows.Media.Brush color)
    {
        var exp = new Expander
        {
            Header = title,
            Foreground = color,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new(0, 0, 0, 6),
            IsExpanded = false
        };
        var lb = new System.Windows.Controls.ListBox
        {
            Margin = new(0, 4, 0, 0),
            MaxHeight = 180,
            FontSize = 12
        };
        var dt = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.MarginProperty, new Thickness(2, 4, 2, 4));

        var row1 = new FrameworkElementFactory(typeof(DockPanel));
        var fileTb = new FrameworkElementFactory(typeof(TextBlock));
        fileTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("FileName"));
        fileTb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        fileTb.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var sugTb = new FrameworkElementFactory(typeof(TextBlock));
        sugTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Suggested"));
        sugTb.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Red);
        sugTb.SetValue(DockPanel.DockProperty, Dock.Right);
        sugTb.SetValue(TextBlock.FontSizeProperty, 11.0);
        row1.AppendChild(sugTb);
        row1.AppendChild(fileTb);
        factory.AppendChild(row1);

        var line2 = new FrameworkElementFactory(typeof(TextBlock));
        line2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Line2"));
        line2.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Gray);
        line2.SetValue(TextBlock.FontSizeProperty, 11.0);
        factory.AppendChild(line2);

        dt.VisualTree = factory;
        lb.ItemTemplate = dt;
        lb.ItemsSource = items.Select(i => new
        {
            FileName = i.Photo.FileName,
            Suggested = (i.Category is "A" or "B" or "F") ? (_isEnglish ? $"-> {i.FileDate}" : $"→ {i.FileDate}") : "",
            Line2 = _isEnglish
              ? $"  EXIF: {i.ExifDate ?? "(none)"}  File: {i.FileDate}  Created: {i.FileCreation:yyyy-MM-dd HH:mm}  Modified: {i.FileModification:yyyy-MM-dd HH:mm}"
              : $"  EXIF: {i.ExifDate ?? "(无)"}  文件: {i.FileDate}  创建: {i.FileCreation:yyyy-MM-dd HH:mm}  修改: {i.FileModification:yyyy-MM-dd HH:mm}"
        });
        exp.Content = lb;
        CategoryPanel.Children.Add(exp);
    }
}
