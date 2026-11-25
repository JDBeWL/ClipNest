using ClipNest.Core.Tagging;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// 把 SigLIP2 的余弦相似度换算成标签。
/// 阈值取自常规图片与插画的分数分布，常见标签的余弦集中在 0.09 到 0.13。
/// </summary>
public static class SiglipSelection
{
    /// <summary>入选标签的余弦下限。</summary>
    public const float CosineFloor = 0.09f;

    /// <summary>分数快照的余弦下限，只用于入库备查。</summary>
    public const float SnapshotCosineFloor = 0.05f;

    /// <summary>对比温度的对数，取自已发布权重的 logit_scale。</summary>
    public const float LogitScale = 4.7244534f;

    /// <summary>对比偏置。该值使绝对概率远低于 0.5，只用于排序与展示。</summary>
    public const float LogitBias = -16.771725f;

    /// <summary>对比温度的指数形式。权重里存的是对数尺度。</summary>
    private static readonly float Temperature = MathF.Exp(LogitScale);

    /// <summary>把余弦转成 0..1 的置信度。</summary>
    public static float ToScore(float cosine) =>
        TagSelection.Sigmoid(Temperature * cosine + LogitBias);

    /// <summary>按余弦下限筛选标签，返回结论与入库快照。</summary>
    public static AiTagResult Select(IReadOnlyList<float> cosines, string[] tags)
    {
        if (cosines.Count != tags.Length)
        {
            throw new InvalidOperationException(
                $"模型输出维度 {cosines.Count} 与标签表 {tags.Length} 不一致");
        }

        var selected = new List<AiTag>(TagSelection.MaxGeneralTags);
        var snapshot = new List<AiTagScore>(256);

        for (var i = 0; i < tags.Length; i++)
        {
            var name = tags[i];
            if (name.Length == 0)
            {
                continue;
            }

            var cosine = cosines[i];
            if (cosine < SnapshotCosineFloor)
            {
                continue;
            }

            var score = ToScore(cosine);
            snapshot.Add(new AiTagScore(i, name, AiTagCategory.General, score));

            if (cosine >= CosineFloor)
            {
                selected.Add(new AiTag(name, AiTagCategory.General, score));
            }
        }

        var top = selected
            .OrderByDescending(tag => tag.Score)
            .Take(TagSelection.MaxGeneralTags)
            .ToList();

        var snapshotRows = snapshot.Count <= TagSelection.MaxSnapshotTags
            ? snapshot.OrderBy(row => row.Index).ToList()
            : snapshot
                .OrderByDescending(row => row.Score)
                .Take(TagSelection.MaxSnapshotTags)
                .OrderBy(row => row.Index)
                .ToList();

        return new AiTagResult(top, snapshotRows);
    }
}
