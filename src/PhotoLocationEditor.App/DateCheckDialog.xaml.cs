using System.Windows;
using System.Windows.Controls;

namespace PhotoLocationEditor.App;

public partial class DateCheckDialog : Window
{
    private readonly List<DateCheckItem> _items;

    public DateCheckDialog(List<DateCheckItem> items)
    {
        _items = items;
        InitializeComponent();

        var suggestFix = items.Where(i => i.Category is "A" or "B" or "F").ToArray();  // actionable
        var ok = items.Where(i => i.Category == "C").ToArray();                          // fine
        var noTime = items.Where(i => i.Category == "E").ToArray();                      // unfixable

        SummaryText.Text = $"共分析 {items.Count} 张：建议处理 {suggestFix.Length} 张 | 无需处理 {ok.Length} 张 | 无可用时间 {noTime.Length} 张";

        if (suggestFix.Length > 0) AddCategory("建议处理 — 将写入校准后的时间", suggestFix, System.Windows.Media.Brushes.DarkOrange);
        if (ok.Length > 0) AddCategory("无需处理 — 时间已正常", ok, System.Windows.Media.Brushes.ForestGreen);
        if (noTime.Length > 0) AddCategory("无可用时间 — 文件时间缺失或全为零，需手动写入", noTime, System.Windows.Media.Brushes.Purple);

        ExecuteABBtn.IsEnabled = suggestFix.Length > 0;
        ExecuteABBtn.Content = "处理建议项";
        ExecuteAllBtn.Content = "全部处理";
        CancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        ExecuteABBtn.Click += (_, _) => { SelectedIds = suggestFix.Select(i => i.Photo.FileName).ToHashSet(); DialogResult = true; Close(); };
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
        lb.ItemsSource = items.Select(i => $"{i.Photo.FileName}\n  EXIF: {i.ExifDate ?? "(无)"}  文件: {i.FileDate}  创建: {i.FileCreation:yyyy-MM-dd HH:mm}  修改: {i.FileModification:yyyy-MM-dd HH:mm}");
        exp.Content = lb;
        CategoryPanel.Children.Add(exp);
    }
}
