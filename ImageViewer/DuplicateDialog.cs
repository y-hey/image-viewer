using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer;

public sealed class DuplicateDialog : Window
{
    public DuplicateDialog(List<List<AssetRecord>> groups)
    {
        Title = "重複ファイル検出結果";
        Width = 600;
        Height = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

        var panel = new DockPanel { Margin = new Thickness(12) };

        var header = new TextBlock
        {
            Text = groups.Count > 0
                ? $"{groups.Count} グループの重複が見つかりました ({groups.Sum(g => g.Count)} ファイル)"
                : "重複ファイルはありません",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var closeBtn = new Button
        {
            Content = "閉じる",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 8, 0, 0)
        };
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Bottom);
        panel.Children.Add(closeBtn);

        var listBox = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };

        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            listBox.Items.Add(new ListBoxItem
            {
                Content = $"--- Group {g + 1} ({group.Count} files, {FormatSize(group[0].FileSize)}) ---",
                Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
                IsEnabled = false
            });
            foreach (var rec in group)
            {
                listBox.Items.Add(new ListBoxItem
                {
                    Content = $"  {rec.Path}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    ToolTip = rec.Path
                });
            }
        }

        panel.Children.Add(listBox);
        Content = panel;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
