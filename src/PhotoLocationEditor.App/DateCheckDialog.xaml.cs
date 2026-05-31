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

        var a = items.Where(i => i.Category == "A").ToArray();
        var b = items.Where(i => i.Category == "B").ToArray();
        var c = items.Where(i => i.Category == "C").ToArray();
        var d = items.Where(i => i.Category == "D").ToArray();

        SummaryText.Text = $"共分析 {items.Count} 张：无拍摄日期 {a.Length} 张 | 建议覆盖 {b.Length} 张 | 保持不变 {c.Length} 张 | 异常 {d.Length} 张";

        if (a.Length > 0) AddCategory("A. 无拍摄日期 — 建议以文件时间写入", a, System.Windows.Media.Brushes.DarkOrange);
        if (b.Length > 0) AddCategory("B. 拍摄日期晚于文件时间 — 建议覆盖", b, System.Windows.Media.Brushes.OrangeRed);
        if (c.Length > 0) AddCategory("C. 拍摄日期 ≤ 文件时间 — 保持不变", c, System.Windows.Media.Brushes.ForestGreen);
        if (d.Length > 0) AddCategory("D. 异常：文件修改时间早于创建时间", d, System.Windows.Media.Brushes.Gray);

        ExecuteABBtn.IsEnabled = a.Length + b.Length > 0;
        CancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        ExecuteABBtn.Click += (_, _) => { SelectedIds = a.Concat(b).Select(i => i.Photo.FileName).ToHashSet(); DialogResult = true; Close(); };
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
