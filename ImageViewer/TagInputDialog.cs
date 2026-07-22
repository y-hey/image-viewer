using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer;

public sealed class TagInputDialog : Window
{
    public string TagName { get; private set; } = "";
    private readonly TextBox _input;

    public TagInputDialog(IReadOnlyList<string> existingTags)
    {
        Title = "タグ追加";
        Width = 320;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

        var panel = new StackPanel { Margin = new Thickness(12) };

        _input = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_input.Text))
            {
                TagName = _input.Text.Trim();
                DialogResult = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
        panel.Children.Add(_input);

        if (existingTags.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "既存タグ (クリックで選択):",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4)
            });

            var wrap = new WrapPanel();
            foreach (var tag in existingTags)
            {
                var btn = new Button
                {
                    Content = tag,
                    Margin = new Thickness(2),
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize = 11,
                    Cursor = Cursors.Hand
                };
                btn.Click += (_, _) => { TagName = tag; DialogResult = true; };
                wrap.Children.Add(btn);
            }
            panel.Children.Add(new ScrollViewer
            {
                Content = wrap,
                MaxHeight = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        }

        Content = panel;
        Loaded += (_, _) => _input.Focus();
    }
}
