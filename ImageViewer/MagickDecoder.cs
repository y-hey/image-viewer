using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace ImageViewer;

public static class MagickDecoder
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".tga", ".exr", ".hdr"
    };

    public static bool CanDecode(string path) => Supported.Contains(Path.GetExtension(path));

    public static BitmapImage? Decode(string path, int maxDimension = 0)
    {
        try
        {
            using var image = new MagickImage(path);
            if (maxDimension > 0 && (image.Width > maxDimension || image.Height > maxDimension))
                image.Thumbnail((uint)maxDimension, (uint)maxDimension);
            image.Format = MagickFormat.Png;
            var bytes = image.ToByteArray();

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
}
