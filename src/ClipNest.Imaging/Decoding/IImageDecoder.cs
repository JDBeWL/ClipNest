using SkiaSharp;

namespace ClipNest.Imaging.Decoding;

public sealed record ImageInfo(int Width, int Height, int Orientation);

public interface IImageDecoder
{
    bool CanDecode(string extension);
    Task<ImageInfo?> GetInfoAsync(string path, CancellationToken ct = default);
    Task<SKBitmap?> DecodeAsync(string path, int maxSize, CancellationToken ct = default);
}
