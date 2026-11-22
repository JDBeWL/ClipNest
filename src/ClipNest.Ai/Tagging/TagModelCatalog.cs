using ClipNest.Core.Tagging;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// 一个已注册的标注模型，含下载管理与推理实例。
/// </summary>
public sealed record TagModelEntry(
    TagModelDefinition Definition,
    TagModelManager Manager,
    ITagger Tagger);

/// <summary>
/// 当前可用的标注模型集合。顺序与 <see cref="TagModelDefinition.All"/> 一致，首个为默认。
/// </summary>
public sealed class TagModelCatalog
{
    public TagModelCatalog(IReadOnlyList<TagModelEntry> models)
    {
        if (models.Count == 0)
        {
            throw new ArgumentException("至少需要一个标注模型。", nameof(models));
        }

        Models = models;
    }

    public IReadOnlyList<TagModelEntry> Models { get; }

    public TagModelEntry Default => Models[0];

    public TagModelEntry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return Models.FirstOrDefault(entry =>
            string.Equals(entry.Definition.Id, id, StringComparison.Ordinal));
    }

    /// <summary>解析模型 ID；未注册或为空时退回默认模型。</summary>
    public TagModelEntry Resolve(string? id) => Find(id) ?? Default;
}
