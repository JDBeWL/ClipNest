using ClipNest.Ai.Models;

namespace ClipNest.Ai.Tagging;

/// <summary>标注模型的推理实现类型。</summary>
public enum TagModelKind
{
    Camie,
    RecognizeAnything,
    Siglip
}

/// <summary>
/// 标注模型定义。权重不随应用分发，首次使用时按需下载。
/// </summary>
public sealed class TagModelDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string License { get; init; }

    public required string Homepage { get; init; }

    public required IReadOnlyList<string> BaseUrls { get; init; }

    public required IReadOnlyList<ModelFile> Files { get; init; }

    public TagModelKind Kind { get; init; } = TagModelKind.Camie;

    public int ImageSize { get; init; } = 512;

    /// <summary>输入是否按 ImageNet 均值与标准差归一化；为假时保持 0..1。</summary>
    public bool NormalizeImageNet { get; init; }

    /// <summary>等比缩放后的补边颜色，与训练时的填充一致。</summary>
    public byte PadRed { get; init; }

    public byte PadGreen { get; init; }

    public byte PadBlue { get; init; }

    /// <summary>
    /// CAMIE v1，覆盖 70,527 个 danbooru 标签，输出角色、作品系列与画面特征。
    /// 许可证为 GPL-3.0，权重仅在本地下载使用，不随应用分发。
    /// </summary>
    public static TagModelDefinition Camie { get; } = new()
    {
        Id = "camie-tagger",
        DisplayName = "CAMIE Tagger v1",
        License = "GPL-3.0",
        Homepage = "https://huggingface.co/Camais03/camie-tagger",
        BaseUrls = new[]
        {
            "https://hf-mirror.com/Camais03/camie-tagger/resolve/main/",
            "https://huggingface.co/Camais03/camie-tagger/resolve/main/"
        },
        Files = new[]
        {
            new ModelFile("model_initial.onnx", "model_initial.onnx", 855_879_045),
            new ModelFile("model_initial_metadata.json", "model_initial_metadata.json", 4_779_633)
        }
    };

    /// <summary>
    /// CAMIE v2，ViT 单阶段模型，覆盖同一份 70,527 标签。
    /// 角色、作品与画师类别的识别指标高于 v1，权重体积约为 v1 的九成。
    /// </summary>
    public static TagModelDefinition CamieV2 { get; } = new()
    {
        Id = "camie-tagger-v2",
        DisplayName = "CAMIE Tagger v2",
        License = "GPL-3.0",
        Homepage = "https://huggingface.co/Camais03/camie-tagger-v2",
        BaseUrls = new[]
        {
            "https://hf-mirror.com/Camais03/camie-tagger-v2/resolve/main/",
            "https://huggingface.co/Camais03/camie-tagger-v2/resolve/main/"
        },
        Files = new[]
        {
            new ModelFile("camie-tagger-v2.onnx", "camie-tagger-v2.onnx", 788_983_561),
            new ModelFile("camie-tagger-v2-metadata.json", "camie-tagger-v2-metadata.json", 7_771_946)
        },
        NormalizeImageNet = true,
        PadRed = 124,
        PadGreen = 116,
        PadBlue = 104
    };

    /// <summary>
    /// RAM++ 通用标注模型，覆盖 4,585 个常见物体与场景标签，适合常规照片。
    /// 许可证为 Apache-2.0，ONNX 图内完成 ImageNet 归一化。
    /// </summary>
    /// <remarks>
    /// 权重取 fp16 版本。fp16 矩阵乘由 DirectML 直接执行；
    /// 量化版本的 MatMulNBits 在 DirectML 上回落到 CPU，GPU 无法承担主要计算。
    /// </remarks>
    public static TagModelDefinition RamPlus { get; } = new()
    {
        Id = "ram-plus-plus",
        DisplayName = "RAM++ 通用识别",
        License = "Apache-2.0",
        Homepage = "https://huggingface.co/AX1Y2JP/recognize-anything-plus-model-ONNX",
        BaseUrls = new[]
        {
            "https://hf-mirror.com/AX1Y2JP/recognize-anything-plus-model-ONNX/resolve/main/",
            "https://huggingface.co/AX1Y2JP/recognize-anything-plus-model-ONNX/resolve/main/"
        },
        Files = new[]
        {
            new ModelFile("onnx/vision_model_fp16.onnx", "vision_model_fp16.onnx", 428_548_973),
            new ModelFile("tag_embeddings.npy", "tag_embeddings.npy", 478_894_208),
            new ModelFile("ram_tag_list.txt", "ram_tag_list.txt", 41_904),
            new ModelFile("ram_tag_list_threshold.txt", "ram_tag_list_threshold.txt", 22_016)
        },
        Kind = TagModelKind.RecognizeAnything,
        ImageSize = 384
    };

    /// <summary>
    /// SigLIP2 零样本标注，用标签文本嵌入与图片嵌入的余弦相似度排序标签。
    /// 许可证为 Apache-2.0；只下载视觉编码器，标签嵌入随程序分发。
    /// </summary>
    /// <remarks>
    /// 文本编码器约 539 MB，只用于一次性生成 4,585 条标签嵌入，不随应用运行。
    /// 标签文本必须补零到 64 个 token，序列长度不同会改变池化结果。
    /// </remarks>
    public static TagModelDefinition Siglip2 { get; } = new()
    {
        Id = "siglip2-base-patch16-224",
        DisplayName = "SigLIP2 零样本",
        License = "Apache-2.0",
        Homepage = "https://huggingface.co/google/siglip2-base-patch16-224",
        BaseUrls = new[]
        {
            "https://hf-mirror.com/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/",
            "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/"
        },
        Files = new[]
        {
            new ModelFile("onnx/vision_model.onnx", "vision_model.onnx", 371_807_752)
        },
        Kind = TagModelKind.Siglip,
        ImageSize = 224
    };

    /// <summary>可选标注模型；顺序决定设置页展示顺序，首个为默认。</summary>
    public static IReadOnlyList<TagModelDefinition> All { get; } =
        new[] { CamieV2, Camie, RamPlus, Siglip2 };

    public static TagModelDefinition Default => CamieV2;

    public string ModelPath(string root) => Path.Combine(root, Files[0].LocalPath);

    public string MetadataPath(string root) => Path.Combine(root, Files[1].LocalPath);

    public string FilePath(string root, string localPath) => Path.Combine(root, localPath);
}
