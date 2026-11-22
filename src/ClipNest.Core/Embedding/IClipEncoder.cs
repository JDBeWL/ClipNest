namespace ClipNest.Core.Embedding;

/// <summary>
/// 语义编码器，把文本或图片编码为归一化向量。
/// 接口放在 Core，避免 Data 反向引用 Ai。
/// </summary>
public interface IClipEncoder
{
    bool IsAvailable { get; }

    Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default);

    Task<float[]> EncodeImageAsync(string path, CancellationToken ct = default);

    /// <summary>卸载模型前释放推理会话；正在编码的请求会先结束。</summary>
    Task ReleaseAsync(CancellationToken ct = default);
}
