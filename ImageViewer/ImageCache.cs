using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageViewer;

public sealed class ImageCache
{
    private readonly ConcurrentDictionary<int, BitmapImage> _items = new();
    private CancellationTokenSource _preCts = new();
    private const int Radius = 5;
    private const int EvictBeyond = 15;

    public BitmapImage? TryGet(int index) =>
        _items.TryGetValue(index, out var img) ? img : null;

    public async Task<BitmapImage?> LoadAsync(int index, string path, int decodeHeight, CancellationToken ct)
    {
        if (_items.TryGetValue(index, out var hit)) return hit;
        var img = await DecodeAsync(path, decodeHeight, ct);
        if (img != null) _items.TryAdd(index, img);
        return img;
    }

    public void PreCacheAround(int center, IReadOnlyList<ImageEntry> list, int decodeHeight)
    {
        _preCts.Cancel();
        _preCts = new CancellationTokenSource();
        var token = _preCts.Token;

        foreach (var k in _items.Keys)
            if (Math.Abs(k - center) > EvictBeyond)
                _items.TryRemove(k, out _);

        _ = Task.Run(async () =>
        {
            for (var d = 1; d <= Radius; d++)
            {
                if (token.IsCancellationRequested) break;
                foreach (var idx in new[] { center + d, center - d })
                {
                    if (token.IsCancellationRequested) break;
                    if (idx < 0 || idx >= list.Count || _items.ContainsKey(idx)) continue;
                    var img = await DecodeAsync(list[idx].FullPath, decodeHeight, token);
                    if (img != null && !token.IsCancellationRequested)
                        _items.TryAdd(idx, img);
                }
            }
        }, token);
    }

    public void Clear()
    {
        _preCts.Cancel();
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
