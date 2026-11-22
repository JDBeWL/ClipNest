using ClipNest.Ai;
using ClipNest.Imaging.Decoding;
using SkiaSharp;

namespace ClipNest.App.Services;

/// <summary>AI 预处理复用系统解码器，可读取 Skia 不支持的 HEIC 与 RAW。</summary>
internal sealed class AiImageSource : IAiImageSource
{
    private readonly IReadOnlyList<IImageDecoder> _decoders;

    public AiImageSource(IEnumerable<IImageDecoder> decoders) => _decoders = decoders.ToArray();

    public async Task<SKBitmap?> LoadAsync(string path, int maxSize, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path);
        foreach (var decoder in _decoders)
        {
            if (!decoder.CanDecode(extension)) continue;

            var bitmap = await decoder.DecodeAsync(path, maxSize, ct).ConfigureAwait(false);
            if (bitmap is not null) return bitmap;
        }

        return null;
    }
}
