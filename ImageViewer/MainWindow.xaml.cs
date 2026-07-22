using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif",
        ".ico", ".webp", ".dds", ".wdp", ".jxr"
    };

    private string _root = "";
    private List<ImageEntry> _master = [];
    private List<ImageEntry> _folderFiltered = [];
    private List<ImageEntry> _display = [];
    private readonly ImageCache _cache = new();
    private CancellationTokenSource _cts = new();
    private AssetDatabase? _db;
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private bool _isGridMode;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && Directory.Exists(args[1]))
                await OpenRoot(args[1]);
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        _db?.Dispose();
        base.OnClosed(e);
    }

    // --- Open folder ---

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "フォルダ選択" };
        if (dlg.ShowDialog() == true) await OpenRoot(dlg.FolderName);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (paths.Length == 0) return;
        var dir = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]);
        if (dir != null) await OpenRoot(dir);
    }

    private async Task OpenRoot(string root)
    {
        _db?.Dispose();
        _root = root;
        _activeTagFilters.Clear();
        ThumbnailConverter.ClearCache();
        Title = $"ImageViewer - {Path.GetFileName(root)}";
        PathText.Text = root;
        _cache.Clear();
        StatusText.Text = "スキャン中...";

        var treeTask = Task.Run(() => BuildTree(root));
        var scanTask = Task.Run(() => Scan(root));

        var tree = await treeTask;
        FolderTree.Items.Clear();
        FolderTree.Items.Add(tree);
        FolderTree.UpdateLayout();
        if (FolderTree.ItemContainerGenerator.ContainerFromItem(tree) is TreeViewItem tvi)
        {
            tvi.IsExpanded = true;
            tvi.IsSelected = true;
        }

        _master = await scanTask;
        _folderFiltered = _master;

        _db = new AssetDatabase(root);
        await Task.Run(() => _db.SyncFiles(_master));

        LoadSettings();
        RefreshTagPanel();
        ApplyFilters();
        StatusText.Text = $"{_master.Count} files";
    }

    // --- Tree / Scan ---

    private static FolderNode BuildTree(string path, int depth = 0)
    {
        var node = new FolderNode(path);
        if (depth > 12) return node;
        try
        {
            foreach (var d in Directory.GetDirectories(path))
            {
                if (Path.GetFileName(d) == "_db") continue;
                node.Children.Add(BuildTree(d, depth + 1));
            }
        }
        catch { }
        return node;
    }

    private List<ImageEntry> Scan(string folder)
    {
        try
        {
            var dbDir = Path.Combine(folder, "_db");
            return Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                })
                .Where(f => !f.StartsWith(dbDir, StringComparison.OrdinalIgnoreCase))
                .Where(f => Ext.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ImageEntry(f, Path.GetRelativePath(_root, f)))
                .ToList();
        }
        catch { return []; }
    }

    // --- View mode ---

    private void OnToggleView(object sender, RoutedEventArgs e) => ToggleViewMode();

    private void ToggleViewMode()
    {
        _isGridMode = !_isGridMode;
        var selectedIdx = ImageList.SelectedIndex;

        if (_isGridMode)
        {
            ImageList.ItemTemplate = (DataTemplate)FindResource("GridItemTemplate");
            ImageList.ItemsPanel = (ItemsPanelTemplate)FindResource("GridPanel");
            VirtualizingPanel.SetIsVirtualizing(ImageList, false);
            ViewModeButton.Content = "List (G)";
        }
        else
        {
            ImageList.ItemTemplate = (DataTemplate)FindResource("ListItemTemplate");
            ImageList.ItemsPanel = (ItemsPanelTemplate)FindResource("ListPanel");
            VirtualizingPanel.SetIsVirtualizing(ImageList, true);
            VirtualizingPanel.SetVirtualizationMode(ImageList, VirtualizationMode.Recycling);
            ViewModeButton.Content = "Grid (G)";
        }

        if (selectedIdx >= 0 && selectedIdx < _display.Count)
        {
            ImageList.SelectedIndex = selectedIdx;
            ImageList.ScrollIntoView(ImageList.SelectedItem!);
        }

        StatusRight.Text = _isGridMode ? "Grid" : "List";
    }

    // --- Filtering ---

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode node || string.IsNullOrEmpty(_root)) return;

        if (node.Path == _root)
        {
            _folderFiltered = _master;
        }
        else
        {
            var prefix = Path.GetRelativePath(_root, node.Path) + Path.DirectorySeparatorChar;
            _folderFiltered = _master.Where(x =>
                x.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        ApplyFilters();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var list = _folderFiltered;

        var q = SearchBox?.Text?.Trim() ?? "";
        if (q.Length > 0)
            list = list.Where(e => e.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_activeTagFilters.Count > 0 && _db != null)
        {
            var match = _db.SearchByTags([.. _activeTagFilters]);
            list = list.Where(e => match.Contains(e.RelativePath)).ToList();
        }

        _display = list;
        _cache.Clear();
        ImageList.ItemsSource = list;
        UpdateStatus();

        if (list.Count > 0)
        {
            ImageList.SelectedIndex = 0;
            ImageList.ScrollIntoView(ImageList.SelectedItem!);
            ImageList.Focus();
        }
        else
        {
            PreviewImage.Source = null;
            FileNameText.Text = "";
            DimText.Text = "";
            AssetTagsText.Text = "";
        }
    }

    // --- Preview ---

    private async void OnImageSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();

        if (ImageList.SelectedItem is not ImageEntry entry) return;
        var idx = _display.IndexOf(entry);
        if (idx < 0) return;

        FileNameText.Text = entry.FileName;
        ShowAssetTags(entry);

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var h = (int)Math.Max(PreviewArea.ActualHeight - 30, 400);

        try
        {
            var img = _cache.TryGet(idx) ?? await _cache.LoadAsync(idx, entry.FullPath, h, token);
            if (img != null && !token.IsCancellationRequested)
            {
                PreviewImage.Source = img;
                DimText.Text = $"{img.PixelWidth} x {img.PixelHeight}";
            }
            _cache.PreCacheAround(idx, _display, h);
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateStatus()
    {
        var sel = ImageList.SelectedItems.Count;
        var total = _display.Count;
        if (sel > 1)
            CountText.Text = $"{sel} selected / {total}";
        else if (ImageList.SelectedIndex >= 0)
            CountText.Text = $"{ImageList.SelectedIndex + 1} / {total}";
        else
            CountText.Text = $"{total} files";
    }

    private void ShowAssetTags(ImageEntry entry)
    {
        if (_db == null) { AssetTagsText.Text = ""; return; }
        var id = _db.GetAssetId(entry.RelativePath);
        if (id == null) { AssetTagsText.Text = ""; return; }
        var tags = _db.GetAssetTags(id.Value);
        AssetTagsText.Text = tags.Count > 0 ? string.Join("  ", tags.Select(t => $"[{t}]")) : "";
    }

    // --- Tag management ---

    private void RefreshTagPanel()
    {
        TagPanel.Children.Clear();
        if (_db == null) return;

        foreach (var tag in _db.GetAllTags())
        {
            var active = _activeTagFilters.Contains(tag);
            var btn = new Button
            {
                Content = tag,
                Margin = new Thickness(2),
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(active
                    ? Color.FromRgb(0x09, 0x47, 0x71)
                    : Color.FromRgb(0x3E, 0x3E, 0x42)),
                Foreground = new SolidColorBrush(active ? Colors.White : Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize = 11,
                Cursor = Cursors.Hand,
                Tag = tag
            };
            btn.Click += OnTagFilterClick;
            TagPanel.Children.Add(btn);
        }
    }

    private void OnTagFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!_activeTagFilters.Remove(tag)) _activeTagFilters.Add(tag);
        RefreshTagPanel();
        ApplyFilters();
    }

    private void OnAddTagClick(object sender, RoutedEventArgs e) => AddTagToSelected();

    private void AddTagToSelected()
    {
        var selected = ImageList.SelectedItems.Cast<ImageEntry>().ToList();
        if (selected.Count == 0 || _db == null) return;

        var dlg = new TagInputDialog(_db.GetAllTags()) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.TagName)) return;

        var tag = dlg.TagName.Trim();
        foreach (var entry in selected)
        {
            var id = _db.GetAssetId(entry.RelativePath);
            if (id != null) _db.AddTag(id.Value, tag);
        }

        if (ImageList.SelectedItem is ImageEntry current)
            ShowAssetTags(current);
        RefreshTagPanel();
    }

    private void RemoveTagFromSelected(string tagName)
    {
        var selected = ImageList.SelectedItems.Cast<ImageEntry>().ToList();
        if (selected.Count == 0 || _db == null) return;

        foreach (var entry in selected)
        {
            var id = _db.GetAssetId(entry.RelativePath);
            if (id != null) _db.RemoveTag(id.Value, tagName);
        }

        if (ImageList.SelectedItem is ImageEntry current)
            ShowAssetTags(current);
        RefreshTagPanel();
        if (_activeTagFilters.Contains(tagName)) ApplyFilters();
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = ImageContextMenu;
        var sepIdx = menu.Items.IndexOf(TagMenuSeparator);

        while (menu.Items.Count > sepIdx + 2)
            menu.Items.RemoveAt(sepIdx + 1);

        if (ImageList.SelectedItem is ImageEntry entry && _db != null)
        {
            var id = _db.GetAssetId(entry.RelativePath);
            if (id != null)
            {
                var tags = _db.GetAssetTags(id.Value);
                for (int i = tags.Count - 1; i >= 0; i--)
                {
                    var tag = tags[i];
                    var item = new MenuItem { Header = $"[x] {tag}" };
                    item.Click += (_, _) => RemoveTagFromSelected(tag);
                    menu.Items.Insert(sepIdx + 1, item);
                }
            }
        }
    }

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (ImageList.SelectedItem is not ImageEntry entry) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{entry.FullPath}\""); }
        catch { }
    }

    // --- Duplicate detection ---

    private async Task DetectDuplicatesAsync()
    {
        if (_db == null || string.IsNullOrEmpty(_root)) return;

        StatusText.Text = "ハッシュ計算中...";
        var unhashed = _db.GetAssetsWithoutHash();

        if (unhashed.Count > 0)
        {
            var root = _root;
            var db = _db;
            await Task.Run(() =>
            {
                foreach (var rec in unhashed)
                {
                    var fullPath = Path.Combine(root, rec.Path);
                    if (File.Exists(fullPath))
                        db.ComputeAndStoreHash(rec.Id, fullPath);
                }
            });
        }

        var groups = _db.FindDuplicates();
        StatusText.Text = groups.Count > 0
            ? $"{groups.Count} 重複グループ検出"
            : $"{_master.Count} files";

        var dlg = new DuplicateDialog(groups) { Owner = this };
        dlg.ShowDialog();
    }

    // --- Settings persistence (stored in _db/ for portability) ---

    private string SettingsPath => Path.Combine(_root, "_db", "viewer-settings.json");

    private void SaveSettings()
    {
        if (string.IsNullOrEmpty(_root)) return;
        try
        {
            var settings = new ViewerSettings
            {
                IsGridMode = _isGridMode,
                WindowWidth = Width,
                WindowHeight = Height,
                LeftPaneWidth = ((Grid)Content).FindName("FolderTree") is FrameworkElement
                    ? ((Grid)((Grid)Content).Children[1]).ColumnDefinitions[0].Width.Value
                    : 220
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var s = JsonSerializer.Deserialize<ViewerSettings>(json);
            if (s == null) return;

            if (s.IsGridMode && !_isGridMode) ToggleViewMode();
            if (s.WindowWidth > 0) Width = s.WindowWidth;
            if (s.WindowHeight > 0) Height = s.WindowHeight;
        }
        catch { }
    }

    // --- Keyboard ---

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O: OnOpenClick(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.F: SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
                case Key.A:
                    ImageList.SelectAll();
                    e.Handled = true; return;
                case Key.D:
                    _ = DetectDuplicatesAsync();
                    e.Handled = true; return;
            }
        }

        if (SearchBox.IsFocused)
        {
            if (e.Key == Key.Escape) { SearchBox.Text = ""; ImageList.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down) { ImageList.Focus(); e.Handled = true; }
            return;
        }

        if (FolderTree.IsKeyboardFocusWithin) { base.OnPreviewKeyDown(e); return; }

        if (e.Key == Key.Oem2 || e.Key == Key.OemQuestion)
        {
            ToggleHelpOverlay();
            e.Handled = true;
            return;
        }

        if (HelpOverlay.Visibility == Visibility.Visible)
        {
            HelpOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.J: case Key.Down:
                Move(1); e.Handled = true; break;
            case Key.K: case Key.Up:
                Move(-1); e.Handled = true; break;
            case Key.Space:
                Move(1); e.Handled = true; break;
            case Key.Back:
                Move(-1); e.Handled = true; break;
            case Key.PageDown:
                Move(_isGridMode ? 20 : 10); e.Handled = true; break;
            case Key.PageUp:
                Move(_isGridMode ? -20 : -10); e.Handled = true; break;
            case Key.Home:
                JumpTo(0); e.Handled = true; break;
            case Key.End:
                JumpTo(_display.Count - 1); e.Handled = true; break;
            case Key.Enter:
                OpenExternal(); e.Handled = true; break;
            case Key.Tab:
                FolderTree.Focus(); e.Handled = true; break;
            case Key.T:
                AddTagToSelected(); e.Handled = true; break;
            case Key.G:
                ToggleViewMode(); e.Handled = true; break;
            case Key.Escape:
                _activeTagFilters.Clear(); SearchBox.Text = "";
                RefreshTagPanel(); ApplyFilters();
                e.Handled = true; break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void ToggleHelpOverlay()
    {
        HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnHelpOverlayClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void Move(int delta)
    {
        if (_display.Count == 0) return;
        JumpTo(Math.Clamp(ImageList.SelectedIndex + delta, 0, _display.Count - 1));
    }

    private void JumpTo(int idx)
    {
        if (idx < 0 || idx >= _display.Count) return;
        ImageList.SelectedIndex = idx;
        ImageList.ScrollIntoView(ImageList.SelectedItem!);
    }

    private void OpenExternal()
    {
        if (ImageList.SelectedItem is not ImageEntry entry) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.FullPath,
                UseShellExecute = true
            });
        }
        catch { }
    }
}

public sealed record ImageEntry(string FullPath, string RelativePath)
{
    public string FileName => Path.GetFileName(FullPath);
}

public class FolderNode
{
    public string Path { get; }
    public string Name { get; }
    public List<FolderNode> Children { get; } = [];

    public FolderNode(string path)
    {
        Path = path;
        var n = System.IO.Path.GetFileName(path);
        Name = string.IsNullOrEmpty(n) ? path : n;
    }
}

public record ViewerSettings
{
    public bool IsGridMode { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double LeftPaneWidth { get; set; }
}
