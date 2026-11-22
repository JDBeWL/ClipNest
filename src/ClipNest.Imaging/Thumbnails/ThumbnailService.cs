using ClipNest.Data.Repositories;
using ClipNest.Imaging.Decoding;
using SkiaSharp;

namespace ClipNest.Imaging.Thumbnails;

public interface IThumbnailPipeline
{
    Task<string?> GetOrCreateAsync(
        long assetId,
        string sha256,
        string sourcePath,
        string extension,
        int size,
        CancellationToken ct = default);
}

public sealed class ThumbnailService : IThumbnailPipeline
{
    private readonly IEnumerable<IImageDecoder> _decoders;
    private readonly ThumbnailCache _cache;
    private readonly ThumbnailRepository _repository;

    public ThumbnailService(
        IEnumerable<IImageDecoder> decoders,
        ThumbnailCache cache,
        ThumbnailRepository repository)
    {
        _decoders = decoders;
        _cache = cache;
        _repository = repository;
    }

    public async Task<string?> GetOrCreateAsync(
        long assetId,
        string sha256,
        string sourcePath,
        string extension,
        int size,
        CancellationToken ct = default)
    {
        var cachePath = _cache.GetPath(sha256, size);
        if (File.Exists(cachePath)) return cachePath;

        var decoder = _decoders.FirstOrDefault(d => d.CanDecode(extension));
        if (decoder is null) return null;

        // 调用方通常是 UI 线程；解码、编码与落盘都不能回到它的同步上下文，
        // 否则首次浏览时每张图都会阻塞主线程一次。
        using var bitmap = await decoder.DecodeAsync(sourcePath, size, ct).ConfigureAwait(false);
        if (bitmap is null) return null;

        await Task.Run(() =>
        {
            var tempPath = cachePath + ".tmp";
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Webp, 80))
            using (var stream = File.Create(tempPath))
            {
                data.SaveTo(stream);
            }

            File.Move(tempPath, cachePath, true);
        }, ct).ConfigureAwait(false);

        await _repository.UpsertAsync(
            assetId, size, cachePath, bitmap.Width, bitmap.Height, "decoded", ct).ConfigureAwait(false);
        return cachePath;
    }
}
