using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private List<ImageEntry> _display = [];
    private readonly ImageCache _cache = new();
    private CancellationTokenSource _cts = new();

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

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "フォルダ選択" };
        if (dlg.ShowDialog() == true)
            await OpenRoot(dlg.FolderName);
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
        _root = root;
        Title = $"ImageViewer - {Path.GetFileName(root)}";
        PathText.Text = root;
        _cache.Clear();

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
        ApplyList(_master);
    }

    private static FolderNode BuildTree(string path, int depth = 0)
    {
        var node = new FolderNode(path);
        if (depth > 12) return node;
        try
        {
            foreach (var d in Directory.GetDirectories(path))
                node.Children.Add(BuildTree(d, depth + 1));
        }
        catch { }
        return node;
    }

    private List<ImageEntry> Scan(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                })
                .Where(f => Ext.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ImageEntry(f, Path.GetRelativePath(_root, f)))
                .ToList();
        }
        catch { return []; }
    }

    private void ApplyList(List<ImageEntry> list)
    {
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
        }
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode node || string.IsNullOrEmpty(_root)) return;

        if (node.Path == _root)
        {
            ApplyList(_master);
            return;
        }

        var prefix = Path.GetRelativePath(_root, node.Path);
        var sep = prefix + Path.DirectorySeparatorChar;
        ApplyList(_master.Where(x =>
            x.RelativePath.StartsWith(sep, StringComparison.OrdinalIgnoreCase) ||
            x.RelativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private async void OnImageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ImageList.SelectedItem is not ImageEntry entry) return;
        var idx = ImageList.SelectedIndex;
        UpdateStatus();
        FileNameText.Text = Path.GetFileName(entry.FullPath);

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
        var i = ImageList.SelectedIndex;
        CountText.Text = i >= 0 ? $"{i + 1} / {_display.Count}" : $"{_display.Count} files";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OnOpenClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (FolderTree.IsKeyboardFocusWithin) { base.OnPreviewKeyDown(e); return; }

        switch (e.Key)
        {
            case Key.J: case Key.Down: case Key.Space:
                Move(1); e.Handled = true; break;
            case Key.K: case Key.Up: case Key.Back:
                Move(-1); e.Handled = true; break;
            case Key.PageDown:
                Move(10); e.Handled = true; break;
            case Key.PageUp:
                Move(-10); e.Handled = true; break;
            case Key.Home:
                JumpTo(0); e.Handled = true; break;
            case Key.End:
                JumpTo(_display.Count - 1); e.Handled = true; break;
            case Key.Enter:
                OpenExternal(); e.Handled = true; break;
            case Key.Tab:
                FolderTree.Focus(); e.Handled = true; break;
        }
        base.OnPreviewKeyDown(e);
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

public sealed record ImageEntry(string FullPath, string RelativePath);

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
