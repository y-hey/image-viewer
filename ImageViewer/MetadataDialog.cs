using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer;

public sealed class MetadataDialog : Window
{
    private static readonly string[] AssetTypes =
        ["", "texture", "sprite", "spritesheet", "tileset", "model", "audio_sfx", "audio_bgm", "audio_ambient", "font", "ui", "vfx", "material", "animation", "shader"];
    private static readonly string[] Usages =
        ["", "character", "environment", "ui", "effect", "ambient", "system", "menu", "hud"];

    private readonly ComboBox _typeCombo;
    private readonly ComboBox _usageCombo;
    private readonly TextBox _notesBox;

    public AssetMeta Result { get; private set; }

    public MetadataDialog(AssetMeta? existing, string fileName)
    {
        Title = $"メタデータ - {fileName}";
        Width = 400;
        Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
        Result = existing ?? new AssetMeta("", "", "", "");

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(MakeLabel("タイプ:"));
        _typeCombo = MakeCombo(AssetTypes, existing?.AssetType ?? "");
        panel.Children.Add(_typeCombo);

        panel.Children.Add(MakeLabel("用途:"));
        _usageCombo = MakeCombo(Usages, existing?.Usage ?? "");
        panel.Children.Add(_usageCombo);

        panel.Children.Add(MakeLabel("ノート:"));
        _notesBox = new TextBox
        {
            Text = existing?.Notes ?? "",
            Height = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_notesBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveBtn = MakeButton("保存");
        saveBtn.Click += (_, _) =>
        {
            Result = new AssetMeta(
                _typeCombo.Text,
                _usageCombo.Text,
                _notesBox.Text.Trim(),
                existing?.MetadataJson ?? "");
            DialogResult = true;
        };
        var cancelBtn = MakeButton("キャンセル");
        cancelBtn.Click += (_, _) => DialogResult = false;
        btnPanel.Children.Add(saveBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);

        Content = panel;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        FontSize = 12,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static ComboBox MakeCombo(string[] items, string selected)
    {
        var combo = new ComboBox
        {
            IsEditable = true,
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(4, 2, 4, 2)
        };
        foreach (var item in items) combo.Items.Add(item);
        combo.Text = selected;
        return combo;
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(16, 4, 16, 4),
        Margin = new Thickness(4, 0, 0, 0),
        Background = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        Cursor = Cursors.Hand
    };
}
