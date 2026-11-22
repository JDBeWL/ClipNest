namespace ClipNest.Core.Tagging;

/// <summary>
/// AI 标签类别，沿用 danbooru 的分类。
/// </summary>
public static class AiTagCategory
{
    public const string General = "general";
    public const string Character = "character";
    public const string Series = "copyright";
    public const string Artist = "artist";
}

/// <summary>
/// 一条识别结果，含标签名、类别与置信度。
/// </summary>
public sealed record AiTag(string Name, string Category, float Score)
{
    /// <summary>booru 标签用下划线分词，展示时将下划线替换为空格。</summary>
    public string DisplayName => Name.Replace('_', ' ');

    public bool IsCharacter => Category == AiTagCategory.Character;
    public bool IsSeries => Category == AiTagCategory.Series;
    public bool IsFeature => Category == AiTagCategory.General;
}

/// <summary>
/// 一条原始置信度，含标签在模型词表中的下标、名字、类别与分数。
/// 名字与类别只用于写入词表；快照行本身只保留下标与分数。
/// </summary>
public readonly record struct AiTagScore(int Index, string Name, string Category, float Score);

/// <summary>
/// 单张图片的识别结果，按类别分组。
/// <see cref="Scores"/> 为该图在阈值之上的完整分数快照，只用于入库；
/// 界面按 <see cref="Tags"/> 展示结论。
/// </summary>
public sealed record AiTagResult(
    IReadOnlyList<AiTag> Tags,
    IReadOnlyList<AiTagScore>? AllScores = null)
{
    public static AiTagResult Empty { get; } = new(Array.Empty<AiTag>());

    /// <summary>完整分数快照；模型没给时为空的只读列表。</summary>
    public IReadOnlyList<AiTagScore> Scores => AllScores ?? Array.Empty<AiTagScore>();

    /// <summary>置信度最高的角色；没有可信角色时为 null。</summary>
    public AiTag? Character => Tags.FirstOrDefault(tag => tag.Category == AiTagCategory.Character);

    /// <summary>所属作品系列。</summary>
    public AiTag? Series => Tags.FirstOrDefault(tag => tag.Category == AiTagCategory.Series);

    public IEnumerable<AiTag> FeatureTags => Tags.Where(tag => tag.Category == AiTagCategory.General);
}

/// <summary>
/// 标注流水线版本。预处理或标签筛选规则改变时递增，
/// 已有资产会按新规则重新入队，而不是继续沿用旧结果。
/// </summary>
public static class TaggingPipeline
{
    public const int Version = 2;
}

/// <summary>
/// 图片标注模型，把一张图片转换为角色、作品与特征标签。
/// </summary>
public interface ITagger : IDisposable
{
    bool IsAvailable { get; }

    Task<AiTagResult> TagAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 长时间空闲后释放会话内存。模型本身有几百 MB，常驻没有意义。
    /// </summary>
    Task ReleaseIfIdleAsync(TimeSpan idleFor, CancellationToken ct = default);
}
