namespace ClipNest.Ai.Models;

/// <summary>
/// 模型文件描述，含远端相对路径、本地文件名与预期字节数。
/// </summary>
public sealed record ClipModelFile(string RemotePath, string LocalPath, long ExpectedBytes);

/// <summary>
/// 语义模型定义。
/// </summary>
public sealed class ClipModelDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required int Dimension { get; init; }

    public required string License { get; init; }

    public required IReadOnlyList<string> BaseUrls { get; init; }

    public required IReadOnlyList<ClipModelFile> Files { get; init; }

    /// <summary>
    /// Chinese-CLIP ViT-B/16 量化版（Apache-2.0）。
    /// </summary>
    public static ClipModelDefinition ChineseClipVitB16 { get; } = new()
    {
        Id = "chinese-clip-vit-base-patch16",
        DisplayName = "Chinese-CLIP ViT-B/16",
        Dimension = 512,
        License = "Apache-2.0",
        BaseUrls = new[]
        {
            "https://hf-mirror.com/Xenova/chinese-clip-vit-base-patch16/resolve/main/",
            "https://huggingface.co/Xenova/chinese-clip-vit-base-patch16/resolve/main/"
        },
        Files = new[]
        {
            new ClipModelFile("onnx/model_quantized.onnx", "model_quantized.onnx", 190_842_404),
            new ClipModelFile("vocab.txt", "vocab.txt", 109_540)
        }
    };
}
