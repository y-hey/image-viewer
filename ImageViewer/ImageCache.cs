using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageViewer;

public sealed class ImageCache
{
    private readonly ConcurrentDictionary<string, BitmapImage> _items = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _preCts = new();
    private const int Radius = 5;
    private const int EvictBeyond = 15;

    public BitmapImage? TryGet(string path) =>
        _items.TryGetValue(path, out var img) ? img : null;

    public async Task<BitmapImage?> LoadAsync(string path, int decodeHeight, CancellationToken ct)
    {
        if (_items.TryGetValue(path, out var hit)) return hit;
        var img = await DecodeAsync(path, decodeHeight, ct);
        if (img != null) _items.TryAdd(path, img);
        return img;
    }

    public void PreCacheAround(int center, IReadOnlyList<ImageEntry> list, int decodeHeight)
    {
        _preCts.Cancel();
        _preCts.Dispose();
        _preCts = new CancellationTokenSource();
        var token = _preCts.Token;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = Math.Max(0, center - EvictBeyond); i <= Math.Min(list.Count - 1, center + EvictBeyond); i++)
            keep.Add(list[i].FullPath);
        foreach (var k in _items.Keys)
            if (!keep.Contains(k))
                _items.TryRemove(k, out _);

        _ = Task.Run(async () =>
        {
            for (var d = 1; d <= Radius; d++)
            {
                if (token.IsCancellationRequested) break;
                foreach (var idx in new[] { center + d, center - d })
                {
                    if (token.IsCancellationRequested) break;
                    if (idx < 0 || idx >= list.Count) continue;
                    var path = list[idx].FullPath;
                    if (_items.ContainsKey(path)) continue;
                    var img = await DecodeAsync(path, decodeHeight, token);
                    if (img != null && !token.IsCancellationRequested)
                        _items.TryAdd(path, img);
                }
            }
        }, token);
    }

    public void Clear()
    {
        _preCts.Cancel();
        _preCts.Dispose();
        _preCts = new CancellationTokenSource();
        _items.Clear();
    }

    private static async Task<BitmapImage?> DecodeAsync(string path, int decodeHeight, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (ct.IsCancellationRequested) return null;
            return await Task.Run(() =>
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeHeight > 0) bi.DecodePixelHeight = decodeHeight;
                bi.StreamSource = new MemoryStream(bytes);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }, ct);
        }
        catch { return null; }
    }
}
