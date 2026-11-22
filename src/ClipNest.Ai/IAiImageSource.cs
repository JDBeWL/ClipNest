using SkiaSharp;

namespace ClipNest.Ai;

/// <summary>
/// 为 AI 预处理提供位图。应用层用系统解码器补齐 Skia 不支持的 HEIC 与 RAW。
/// </summary>
public interface IAiImageSource
{
    /// <summary>按长边上限解码；无法识别时返回空值。</summary>
    Task<SKBitmap?> LoadAsync(string path, int maxSize, CancellationToken ct = default);
}

/// <summary>默认实现，只处理 Skia 能识别的格式。</summary>
internal sealed class SkiaAiImageSource : IAiImageSource
{
    public static SkiaAiImageSource Instance { get; } = new();

    public Task<SKBitmap?> LoadAsync(string path, int maxSize, CancellationToken ct = default) =>
        Task.Run<SKBitmap?>(() =>
        {
            try
            {
                return SKBitmap.Decode(path);
            }
            catch
            {
                return null;
            }
        }, ct);
}
