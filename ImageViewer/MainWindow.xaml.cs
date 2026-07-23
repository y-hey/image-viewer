using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif",
        ".ico", ".webp", ".dds", ".wdp", ".jxr",
        ".psd", ".tga", ".exr", ".hdr"
    };
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".ogg", ".flac", ".aac", ".wma"
    };
    private static readonly HashSet<string> FontExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf"
    };
    private static readonly HashSet<string> AllExt = [.. ImageExt, .. AudioExt, .. FontExt];

    private string _root = "";
    private List<ImageEntry> _master = [];
    private List<ImageEntry> _folderFiltered = [];
    private List<ImageEntry> _display = [];
    private readonly ImageCache _cache = new();
    private CancellationTokenSource _cts = new();
    private AssetDatabase? _db;
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private bool _isGridMode = true;
    private Point _panStart;
    private bool _isPanning;
    private int _bgIndex;
    private static readonly Brush[] BgBrushes = [CreateBrush(0x18), CreateCheckerBrush(), Brushes.White, CreateBrush(0x80)];
    private static readonly string[] BgNames = ["Dark", "Checker", "White", "Gray"];
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _hoverPlayingPath;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;
    private FolderLock? _lock;

    public MainWindow()
    {
        InitializeComponent();
        _audioTimer.Tick += (_, _) =>
        {
            if (_player.NaturalDuration.HasTimeSpan)
            {
                var pos = _player.Position;
                var dur = _player.NaturalDuration.TimeSpan;
                AudioTime.Text = $"{pos:mm\\:ss} / {dur:mm\\:ss}";
            }
        };
        _player.MediaEnded += (_, _) => Dispatcher.Invoke(() =>
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
        });
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && Directory.Exists(args[1]))
                await OpenRoot(args[1]);
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAudio();
        SaveSettings();
        _watcher?.Dispose();
        _db?.Dispose();
        _lock?.Dispose();
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
        _lock?.Dispose();
        _db?.Dispose();

        var (lk, lockErr) = FolderLock.TryAcquire(root);
        if (lockErr != null)
        {
            var result = MessageBox.Show(this, lockErr + "\n\n強制的に開きますか？", "ロック検出",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            (lk, _) = FolderLock.ForceAcquire(root);
        }
        _lock = lk;

        _root = root;
        _activeTagFilters.Clear();
        ThumbnailConverter.SetRootPath(root);
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

        SetupWatcher(root);
        LoadSettings();
        RefreshTagPanel();
        ApplyFilters();
        StatusText.Text = $"{_master.Count} files";
    }

    private void SetupWatcher(string root)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
        };
        _watcher.Created += OnFsChanged;
        _watcher.Deleted += OnFsChanged;
        _watcher.Renamed += (_, _) => ScheduleRescan();
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e) => ScheduleRescan();

    private void ScheduleRescan()
    {
        Dispatcher.Invoke(() =>
        {
            if (_watcherDebounce == null)
            {
                _watcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _watcherDebounce.Tick += async (_, _) =>
                {
                    _watcherDebounce!.Stop();
                    var newList = await Task.Run(() => Scan(_root));
                    _master = newList;
                    ReapplyTreeFilter();
                    if (_db != null) await Task.Run(() => _db.SyncFiles(_master));
                    ApplyFilters();
                    StatusText.Text = $"{_master.Count} files";
                };
            }
            _watcherDebounce.Stop();
            _watcherDebounce.Start();
        });
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
            var dbDir = Path.Combine(folder, "_db") + Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                })
                .Where(f => !f.StartsWith(dbDir, StringComparison.OrdinalIgnoreCase))
                .Where(f => AllExt.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    var info = new FileInfo(f);
                    return new ImageEntry(f, Path.GetRelativePath(_root, f), info.Length, info.LastWriteTimeUtc);
                })
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
            ViewModeButton.Content = "List (G)";
        }
        else
        {
            ImageList.ItemTemplate = (DataTemplate)FindResource("ListItemTemplate");
            ImageList.ItemsPanel = (ItemsPanelTemplate)FindResource("ListPanel");
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
        ReapplyTreeFilter();
        ApplyFilters();
    }

    private void ReapplyTreeFilter()
    {
        if (FolderTree.SelectedItem is FolderNode node && node.Path != _root)
        {
            var prefix = Path.GetRelativePath(_root, node.Path) + Path.DirectorySeparatorChar;
            _folderFiltered = _master.Where(x =>
                x.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            _folderFiltered = _master;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilters();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilters();
    }

    private List<ImageEntry> ApplySort(List<ImageEntry> list) => SortCombo?.SelectedIndex switch
    {
        1 => list.OrderByDescending(e => e.FileSize).ToList(),
        2 => list.OrderByDescending(e => e.ModifiedAt).ToList(),
        3 => list.OrderBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
        _ => list.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
    };

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

        var sorted = ApplySort(list);

        bool changed = sorted.Count != _display.Count;
        if (!changed && sorted.Count > 0)
            changed = sorted[0].FullPath != _display[0].FullPath
                   || sorted[^1].FullPath != _display[^1].FullPath;

        _display = sorted;
        UpdateStatus();

        if (!changed) return;

        ImageList.ItemsSource = _display;

        if (_display.Count > 0)
        {
            ImageList.SelectedIndex = 0;
            ImageList.ScrollIntoView(ImageList.SelectedItem!);
            if (!FolderTree.IsKeyboardFocusWithin)
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
        var idx = ImageList.SelectedIndex;
        if (idx < 0) return;

        FileNameText.Text = entry.FileName;
        ShowAssetTags(entry);
        DimText.Text = "";

        var ext = Path.GetExtension(entry.FullPath);
        SetPreviewMode(ext);

        if (AudioExt.Contains(ext))
        {
            AudioInfo.Text = entry.FileName;
            return;
        }

        if (FontExt.Contains(ext))
        {
            ShowFontPreview(entry.FullPath);
            return;
        }

        ResetZoom();
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var h = (int)Math.Max(PreviewArea.ActualHeight - 30, 400);

        try
        {
            var img = _cache.TryGet(entry.FullPath) ?? await _cache.LoadAsync(entry.FullPath, h, token);
            if (img != null && !token.IsCancellationRequested)
            {
                PreviewImage.Source = img;
                DimText.Text = $"{img.PixelWidth} x {img.PixelHeight}";
            }
            _cache.PreCacheAround(idx, _display, h);
        }
        catch (OperationCanceledException) { }
    }

    private void SetPreviewMode(string extension)
    {
        bool isImage = ImageExt.Contains(extension);
        bool isAudio = AudioExt.Contains(extension);
        bool isFont = FontExt.Contains(extension);

        PreviewBorder.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;
        FontPanel.Visibility = isFont ? Visibility.Visible : Visibility.Collapsed;

        if (!isAudio) StopAudio();
        if (!isImage) PreviewImage.Source = null;
    }

    // --- Audio preview ---

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (ImageList.SelectedItem is not ImageEntry entry) return;
        _player.Open(new Uri(entry.FullPath));
        _player.Play();
        PlayBtn.Content = "▶ Playing";
        _audioTimer.Start();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopAudio();

    private void StopAudio()
    {
        _player.Stop();
        _player.Close();
        _audioTimer.Stop();
        _hoverPlayingPath = null;
        PlayBtn.Content = "▶ Play";
        AudioTime.Text = "";
    }

    private ListBoxItem? _lastHoverItem;

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        var hit = ImageList.InputHitTest(e.GetPosition(ImageList)) as DependencyObject;
        while (hit != null && hit is not ListBoxItem)
            hit = VisualTreeHelper.GetParent(hit);

        if (hit is ListBoxItem item)
        {
            if (item == _lastHoverItem) return;
            _lastHoverItem = item;

            if (item.Content is ImageEntry entry && AudioExt.Contains(Path.GetExtension(entry.FullPath)))
            {
                if (_hoverPlayingPath != entry.FullPath)
                {
                    _player.Open(new Uri(entry.FullPath));
                    _player.Play();
                    _audioTimer.Start();
                    _hoverPlayingPath = entry.FullPath;
                    PlayBtn.Content = "▶ Playing";
                }
                return;
            }
        }
        else
        {
            _lastHoverItem = null;
        }

        if (_hoverPlayingPath != null) StopAudio();
    }

    private void OnListMouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverPlayingPath != null) StopAudio();
    }

    // --- Font preview ---

    private void ShowFontPreview(string fontPath)
    {
        try
        {
            var glyph = new GlyphTypeface(new Uri(fontPath));
            var familyName = glyph.FamilyNames.Values.FirstOrDefault() ?? "Unknown";
            FontInfoText.Text = familyName;
            DimText.Text = familyName;

            var dir = Path.GetDirectoryName(fontPath)!.Replace('\\', '/');
            var fontFamily = new FontFamily(
                new Uri("file:///" + dir + "/"),
                "./" + Path.GetFileName(fontPath) + "#" + familyName);
            FontSample1.FontFamily = fontFamily;
            FontSample2.FontFamily = fontFamily;
            FontSample3.FontFamily = fontFamily;
        }
        catch
        {
            FontInfoText.Text = "フォントの読み込みに失敗";
            FontSample1.FontFamily = FontSample2.FontFamily = FontSample3.FontFamily = SystemFonts.MessageFontFamily;
        }
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

    // --- Metadata ---

    private void EditMetadata()
    {
        if (ImageList.SelectedItem is not ImageEntry entry || _db == null) return;
        var id = _db.GetAssetId(entry.RelativePath);
        if (id == null) return;

        var existing = _db.GetMetadata(id.Value);
        var dlg = new MetadataDialog(existing, entry.FileName) { Owner = this };
        if (dlg.ShowDialog() == true)
            _db.SetMetadata(id.Value, dlg.Result);
    }

    private void ExportMetadata()
    {
        if (_db == null || string.IsNullOrEmpty(_root)) return;
        var all = _db.ExportAll();
        var export = all.Select(a => new
        {
            path = a.path,
            tags = a.tags,
            type = a.meta?.AssetType ?? "",
            usage = a.meta?.Usage ?? "",
            notes = a.meta?.Notes ?? ""
        }).Where(a => a.tags.Count > 0 || !string.IsNullOrEmpty(a.type) || !string.IsNullOrEmpty(a.notes));

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        var exportPath = Path.Combine(_root, "_db", "asset-catalog.json");
        File.WriteAllText(exportPath, json);
        StatusText.Text = $"Exported to {exportPath}";
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
                LeftPaneWidth = LeftPaneColumn.Width.Value
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
            if (s.LeftPaneWidth > 0) LeftPaneColumn.Width = new GridLength(s.LeftPaneWidth);
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
                case Key.M:
                    EditMetadata(); e.Handled = true; return;
                case Key.E:
                    ExportMetadata(); e.Handled = true; return;
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
            case Key.Down:
                Move(_isGridMode ? GridColumns() : 1); e.Handled = true; break;
            case Key.Up:
                Move(_isGridMode ? -GridColumns() : -1); e.Handled = true; break;
            case Key.Right: case Key.J:
                Move(1); e.Handled = true; break;
            case Key.Left: case Key.K:
                Move(-1); e.Handled = true; break;
            case Key.Space:
                Move(1); e.Handled = true; break;
            case Key.Back:
                Move(-1); e.Handled = true; break;
            case Key.PageDown:
                Move((_isGridMode ? GridColumns() : 1) * 5); e.Handled = true; break;
            case Key.PageUp:
                Move(-(_isGridMode ? GridColumns() : 1) * 5); e.Handled = true; break;
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
            case Key.B:
                CycleBackground(); e.Handled = true; break;
            case Key.F:
                ResetZoom(); e.Handled = true; break;
            case Key.OemPlus: case Key.Add:
                Zoom(1.25); e.Handled = true; break;
            case Key.OemMinus: case Key.Subtract:
                Zoom(1.0 / 1.25); e.Handled = true; break;
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

    // --- Zoom / Pan / Background ---

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.15 : 1.0 / 1.15);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ResetZoom(); e.Handled = true; return; }
        _isPanning = true;
        _panStart = e.GetPosition(PreviewBorder);
        PreviewBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(PreviewBorder);
        PreviewTranslate.X += pos.X - _panStart.X;
        PreviewTranslate.Y += pos.Y - _panStart.Y;
        _panStart = pos;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        PreviewBorder.ReleaseMouseCapture();
    }

    private void Zoom(double factor)
    {
        var s = Math.Clamp(PreviewScale.ScaleX * factor, 0.1, 20.0);
        PreviewScale.ScaleX = PreviewScale.ScaleY = s;
        ZoomText.Text = $"{s * 100:F0}%";
    }

    private void ResetZoom()
    {
        PreviewScale.ScaleX = PreviewScale.ScaleY = 1.0;
        PreviewTranslate.X = PreviewTranslate.Y = 0;
        ZoomText.Text = "";
    }

    private void CycleBackground()
    {
        _bgIndex = (_bgIndex + 1) % BgBrushes.Length;
        PreviewBorder.Background = BgBrushes[_bgIndex];
        StatusRight.Text = $"BG: {BgNames[_bgIndex]}";
    }

    private static SolidColorBrush CreateBrush(byte gray)
    {
        var b = new SolidColorBrush(Color.FromRgb(gray, gray, gray));
        b.Freeze();
        return b;
    }

    private static DrawingBrush CreateCheckerBrush()
    {
        var light = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        light.Freeze();
        var dark = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
        dark.Freeze();
        var rects = new GeometryGroup();
        rects.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        rects.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        rects.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(dark, null, rects));
        group.Freeze();
        var brush = new DrawingBrush { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute, Drawing = group };
        brush.Freeze();
        return brush;
    }

    private int GridColumns() => Math.Max(1, (int)(ImageList.ActualWidth / 114));

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

public sealed record ImageEntry(string FullPath, string RelativePath, long FileSize, DateTime ModifiedAt)
{
    public string FileName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath);
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
