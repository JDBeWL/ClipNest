using ClipNest.Core.Indexing;
using SkiaSharp;

namespace ClipNest.Imaging.Decoding;

public sealed class SkiaImageDecoder : IImageDecoder
{
    public bool CanDecode(string extension) => SupportedFormats.Common.Contains(extension);

    public Task<ImageInfo?> GetInfoAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        using var codec = SKCodec.Create(path);
        return codec is null
            ? null
            : new ImageInfo(codec.Info.Width, codec.Info.Height, (int)codec.EncodedOrigin);
    }, ct);

    public Task<SKBitmap?> DecodeAsync(string path, int maxSize, CancellationToken ct = default) => Task.Run(() =>
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;

        var info = codec.Info;
        var scale = maxSize > 0
            ? Math.Min(1.0, (double)maxSize / Math.Max(info.Width, info.Height))
            : 1.0;

        // 编码器支持的缩放档位有限，先按最接近目标的档位解码，再缩到精确尺寸。
        // 直接解码整张再缩小会让缩略图与 AI 预处理为用不到的分辨率付出时间与内存。
        var decodeSize = scale < 1.0
            ? codec.GetScaledDimensions((float)scale)
            : new SKSizeI(info.Width, info.Height);
        var target = new SKImageInfo(
            decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(target);

        var result = codec.GetPixels(target, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        if (scale >= 1.0) return ApplyOrientation(decoded, codec.EncodedOrigin);

        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
        using var resized = decoded.Resize(new SKImageInfo(width, height, decoded.ColorType, decoded.AlphaType), SKFilterQuality.High);
        return resized is null ? null : ApplyOrientation(resized, codec.EncodedOrigin);
    }, ct);

    internal static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return source.Copy();

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var dest = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(dest);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1);
                canvas.Translate(-source.Width, 0);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, source.Width / 2f, source.Height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1);
                canvas.Translate(0, -source.Height);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Translate(0, -source.Height);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.RotateDegrees(90);
                canvas.Translate(source.Width, -source.Height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.RotateDegrees(-90);
                canvas.Translate(-source.Width, 0);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.RotateDegrees(-90);
                canvas.Scale(-1, 1);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        return dest;
    }
}
