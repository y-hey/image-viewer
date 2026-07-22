using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ImageViewer;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class ThumbnailConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> MemCache = new();
    private static string? _rootPath;
    private static string? _thumbDir;

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
            bi.DecodePixelWidth = 140;
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
