using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ImageViewer;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class ThumbnailConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

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
            Cache.TryAdd(path, bi);
            return bi;
        }
        catch
        {
            Cache.TryAdd(path, null);
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static void ClearCache() => Cache.Clear();
}
