using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class ThumbnailConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> MemCache = new();
    private static string? _rootPath;
    private static string? _thumbDir;
    private static BitmapImage? _audioIcon;

    public static void SetRootPath(string root)
    {
        _rootPath = root;
        _thumbDir = Path.Combine(root, "_db", "thumbs");
        Directory.CreateDirectory(_thumbDir);
        MemCache.Clear();
    }

    public static void ClearCache() => MemCache.Clear();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (MemCache.TryGetValue(path, out var cached)) return cached;

        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext is ".wav" or ".mp3" or ".ogg" or ".flac" or ".aac" or ".wma")
        {
            var icon = _audioIcon ??= GenerateAudioIcon();
            MemCache.TryAdd(path, icon);
            return icon;
        }

        if (ext is ".ttf" or ".otf")
        {
            if (_thumbDir != null)
            {
                var diskPath = GetDiskPath(path);
                if (File.Exists(diskPath))
                {
                    var img = LoadFromDisk(diskPath);
                    if (img != null) { MemCache.TryAdd(path, img); return img; }
                }
            }
            var thumb = GenerateFontThumbnail(path);
            MemCache.TryAdd(path, thumb);
            if (thumb != null) Task.Run(() => SaveToDisk(thumb, path));
            return thumb;
        }

        if (_thumbDir != null)
        {
            var diskPath = GetDiskPath(path);
            if (File.Exists(diskPath))
            {
                var img = LoadFromDisk(diskPath);
                if (img != null) { MemCache.TryAdd(path, img); return img; }
            }
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 100;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            MemCache.TryAdd(path, bi);
            Task.Run(() => SaveToDisk(bi, path));
            return bi;
        }
        catch
        {
            MemCache.TryAdd(path, null);
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static BitmapImage GenerateAudioIcon()
    {
        return RenderIcon(ctx =>
        {
            var text = new FormattedText("♫",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Symbol"), 44,
                new SolidColorBrush(Color.FromRgb(0x66, 0x99, 0xCC)), 96);
            ctx.DrawText(text, new Point((100 - text.Width) / 2, (100 - text.Height) / 2 - 4));

            var label = new FormattedText("AUDIO",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10,
                new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), 96);
            ctx.DrawText(label, new Point((100 - label.Width) / 2, 80));
        });
    }

    private static BitmapImage? GenerateFontThumbnail(string fontPath)
    {
        try
        {
            var glyph = new GlyphTypeface(new Uri(fontPath));
            var familyName = glyph.FamilyNames.Values.FirstOrDefault() ?? "Font";
            var dir = Path.GetDirectoryName(fontPath)!.Replace('\\', '/');
            var fontFamily = new FontFamily(new Uri("file:///" + dir + "/"),
                "./" + Path.GetFileName(fontPath) + "#" + familyName);

            return RenderIcon(ctx =>
            {
                var sample = new FormattedText("Ag",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    42, new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), 96);
                ctx.DrawText(sample, new Point((100 - sample.Width) / 2, (80 - sample.Height) / 2));

                var name = new FormattedText(familyName.Length > 14 ? familyName[..14] : familyName,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 9,
                    new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), 96);
                ctx.DrawText(name, new Point((100 - name.Width) / 2, 82));
            });
        }
        catch { return null; }
    }

    private static BitmapImage RenderIcon(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), null, new Rect(0, 0, 100, 100));
            draw(ctx);
        }
        var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static string GetDiskPath(string fullPath)
    {
        var key = _rootPath != null ? Path.GetRelativePath(_rootPath, fullPath) : fullPath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_thumbDir!, System.Convert.ToHexString(hash)[..16] + ".png");
    }

    private static BitmapImage? LoadFromDisk(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static void SaveToDisk(BitmapImage source, string originalPath)
    {
        if (_thumbDir == null) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            var diskPath = GetDiskPath(originalPath);
            using var fs = new FileStream(diskPath, FileMode.Create);
            encoder.Save(fs);
        }
        catch { }
    }
}
