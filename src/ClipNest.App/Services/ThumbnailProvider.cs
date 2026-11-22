using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using ClipNest.Core.Models;
using ClipNest.Imaging.Thumbnails;
using SkiaSharp;

namespace ClipNest.App.Services;

public sealed class ThumbnailProvider : IThumbnailProvider
{
    // 图库不再限制条数后，解码缓存需要按内存封顶，淘汰后仍可从未变的缩略图文件重新解码。
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private const long MaxBlurBytes = 16L * 1024 * 1024;

    private readonly ThumbnailService _service;
    private readonly ConcurrentDictionary<(string Sha256, int Size), Bitmap> _cache = new();
    private readonly ConcurrentDictionary<(string Sha256, int Size), Task<Bitmap?>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Bitmap> _blurCache = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _blurTasks = new();
    private readonly object _cacheLock = new();
    private readonly object _blurLock = new();
    private readonly LinkedList<(string Sha256, int Size)> _lru = new();
    private readonly Dictionary<(string Sha256, int Size), LinkedListNode<(string Sha256, int Size)>> _lruIndex = new();
    private readonly LinkedList<string> _blurLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _blurLruIndex = new();
    private long _cacheBytes;
    private long _blurBytes;

    public ThumbnailProvider(ThumbnailService service) => _service = service;

    public async Task<Bitmap?> GetAsync(
        AssetListItem item, int size, CancellationToken ct = default)
    {
        var key = (item.Sha256, size);
        if (_cache.TryGetValue(key, out var cached))
        {
            TouchCache(key);
            return cached;
        }

        // 瀑布流补高清、查看器预取与正式打开可能同时要同一张同一档位。
        // 共用一次解码，避免重复解码同一张原图，也避免两个线程写同一个缓存文件。
        var task = _inFlight.GetOrAdd(key, _ => LoadAsync(item, size, ct));
        try
        {
            return await task;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    public Bitmap? GetCached(string sha256)
    {
        Bitmap? best = null;
        var bestWidth = 0;
        foreach (var (key, bitmap) in _cache)
        {
            if (!string.Equals(key.Sha256, sha256, StringComparison.Ordinal)) continue;
            var width = bitmap.PixelSize.Width;
            if (width <= bestWidth) continue;
            best = bitmap;
            bestWidth = width;
        }

        return best;
    }

    private async Task<Bitmap?> LoadAsync(
        AssetListItem item, int size, CancellationToken ct)
    {
        var path = await _service.GetOrCreateAsync(
            item.Id,
            item.Sha256,
            item.PrimaryPath,
            Path.GetExtension(item.PrimaryPath),
            size,
            ct).ConfigureAwait(false);

        if (path is null) return null;

        // 位图解码同样是 CPU 工作；调用方在 UI 线程，直接解码会卡住首屏滚动。
        var bitmap = await Task.Run(() => new Bitmap(path), ct).ConfigureAwait(false);
        StoreCache((item.Sha256, size), bitmap);
        return bitmap;
    }

    public async Task<Bitmap?> GetBlurredAsync(
        AssetListItem item, int size = 32, CancellationToken ct = default)
    {
        var key = $"{item.Sha256}:{size}";
        if (_blurCache.TryGetValue(key, out var cached))
        {
            TouchBlurred(key);
            return cached;
        }

        var task = _blurTasks.GetOrAdd(key, _ => CreateBlurredAsync(item, size, ct));
        try
        {
            var bitmap = await task;
            if (bitmap is not null) StoreBlurred(key, bitmap);
            return bitmap;
        }
        finally
        {
            _blurTasks.TryRemove(key, out _);
        }
    }

    private async Task<Bitmap?> CreateBlurredAsync(
        AssetListItem item, int size, CancellationToken ct)
    {
        // 复用 256 缓存文件，避免为模糊图再解码一次原图。
        var path = await _service.GetOrCreateAsync(
            item.Id,
            item.Sha256,
            item.PrimaryPath,
            Path.GetExtension(item.PrimaryPath),
            256,
            ct).ConfigureAwait(false);
        if (path is null) return null;

        return await Task.Run(() => RenderBlur(path, size), ct).ConfigureAwait(false);
    }

    private static Bitmap? RenderBlur(string path, int size)
    {
        using var source = SKBitmap.Decode(path);
        if (source is null || source.Width <= 0 || source.Height <= 0) return null;

        // 先中心裁成正方形，保证模糊图和瓦片的裁剪区域一致。
        var side = Math.Min(source.Width, source.Height);
        var left = (source.Width - side) / 2f;
        var top = (source.Height - side) / 2f;

        using var target = new SKBitmap(size, size);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        using var filter = SKImageFilter.CreateBlur(size * 0.08f, size * 0.08f);
        using var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true,
            ImageFilter = filter
        };
        canvas.DrawBitmap(
            source,
            new SKRect(left, top, left + side, top + side),
            new SKRect(0, 0, size, size),
            paint);
        canvas.Flush();

        using var image = SKImage.FromBitmap(target);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }

    private void StoreCache((string Sha256, int Size) key, Bitmap bitmap)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var previous))
                _cacheBytes -= BitmapBytes(previous);

            _cache[key] = bitmap;
            _cacheBytes += BitmapBytes(bitmap);

            if (_lruIndex.TryGetValue(key, out var existing))
                _lru.Remove(existing);
            _lruIndex[key] = _lru.AddLast(key);

            while (_cacheBytes > MaxCacheBytes && _lru.First is { } oldest)
            {
                _lru.RemoveFirst();
                _lruIndex.Remove(oldest.Value);
                if (_cache.TryRemove(oldest.Value, out var evicted))
                    _cacheBytes -= BitmapBytes(evicted);
            }
        }
    }

    private void TouchCache((string Sha256, int Size) key)
    {
        lock (_cacheLock)
        {
            if (!_lruIndex.TryGetValue(key, out var node)) return;
            _lru.Remove(node);
            _lru.AddLast(node);
        }
    }

    private void StoreBlurred(string key, Bitmap bitmap)
    {
        lock (_blurLock)
        {
            if (_blurCache.TryGetValue(key, out var previous))
                _blurBytes -= BitmapBytes(previous);

            _blurCache[key] = bitmap;
            _blurBytes += BitmapBytes(bitmap);

            if (_blurLruIndex.TryGetValue(key, out var existing))
                _blurLru.Remove(existing);
            _blurLruIndex[key] = _blurLru.AddLast(key);

            while (_blurBytes > MaxBlurBytes && _blurLru.First is { } oldest)
            {
                _blurLru.RemoveFirst();
                _blurLruIndex.Remove(oldest.Value);
                if (_blurCache.TryRemove(oldest.Value, out var evicted))
                    _blurBytes -= BitmapBytes(evicted);
            }
        }
    }

    private void TouchBlurred(string key)
    {
        lock (_blurLock)
        {
            if (!_blurLruIndex.TryGetValue(key, out var node)) return;
            _blurLru.Remove(node);
            _blurLru.AddLast(node);
        }
    }

    private static long BitmapBytes(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        return (long)size.Width * size.Height * 4;
    }
}
