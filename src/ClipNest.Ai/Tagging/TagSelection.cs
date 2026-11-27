using ClipNest.Core.Tagging;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// 从模型输出的 logits 里选出要保留的标签，阈值策略均为纯函数。
/// </summary>
public static class TagSelection
{
    /// <summary>特征标签阈值。低于此值的多为风格噪声。</summary>
    public const float GeneralThreshold = 0.35f;

    /// <summary>作品系列阈值。</summary>
    public const float SeriesThreshold = 0.30f;

    /// <summary>
    /// 角色标签带作品后缀时，作品头可低于常规阈值。
    /// 后缀是明确证据，而系列头经常把少女前线误标为崩坏、Fate 或 Arknights。
    /// </summary>
    public const float SeriesHintFloor = 0.10f;

    /// <summary>
    /// 角色后缀与 copyright 词表不一致的常见缩写。只收录词表里确实有全名的条目。
    /// </summary>
    private static readonly Dictionary<string, string> SeriesAliases = new(StringComparer.Ordinal)
    {
        ["kancolle"] = "kantai_collection"
    };

    /// <summary>
    /// 角色入库阈值。学园偶像大师这类新角色绝对分只有 0.18~0.30，无法继续提高；
    /// 展示层另有更高的门槛，见界面层。
    /// </summary>
    public const float CharacterFloor = 0.18f;

    public const int MaxGeneralTags = 14;
    public const int MaxCharacterTags = 3;

    /// <summary>
    /// 分数快照的下限。单张图 ≥0.10 的标签平均 258 条，再往下会连同无意义的噪声一起存入；
    /// 0.10 已覆盖全部可用的弱标签。
    /// </summary>
    public const float SnapshotFloor = 0.10f;

    /// <summary>单张图的快照条数上限，防止个别图片把词表大半写入数据库。</summary>
    public const int MaxSnapshotTags = 320;

    /// <summary>
    /// 取一份分数快照，返回阈值之上的全部标签，按下标升序排列，便于批量写入与去重。
    /// 超过上限时按分数截断，再按下标重新排序。
    /// </summary>
    public static IReadOnlyList<AiTagScore> SelectScores(
        IReadOnlyList<float> logits, string[] names, string[] categories) =>
        SelectScoresCore(logits, names, categories, applySigmoid: true, SnapshotFloor);

    /// <summary>
    /// 从已归一化到 0..1 的概率中取分数快照，返回阈值之上的全部标签并按下标升序排列。
    /// </summary>
    public static IReadOnlyList<AiTagScore> SelectScoresFromProbabilities(
        IReadOnlyList<float> probabilities,
        string[] names,
        string[] categories,
        float snapshotFloor = SnapshotFloor) =>
        SelectScoresCore(probabilities, names, categories, applySigmoid: false, snapshotFloor);

    public static AiTagResult Select(IReadOnlyList<float> logits, string[] names, string[] categories) =>
        SelectCore(
            logits, names, categories, applySigmoid: true,
            GeneralThreshold, CharacterFloor, SeriesThreshold, SeriesHintFloor);

    /// <summary>
    /// 按指定阈值从 logits 中筛选标签，用于概率分布偏陡的损失函数变体。
    /// </summary>
    public static AiTagResult Select(
        IReadOnlyList<float> logits,
        string[] names,
        string[] categories,
        float generalThreshold,
        float characterFloor,
        float seriesThreshold = SeriesThreshold,
        float seriesHintFloor = SeriesHintFloor) =>
        SelectCore(
            logits, names, categories, applySigmoid: true,
            generalThreshold, characterFloor, seriesThreshold, seriesHintFloor);

    /// <summary>
    /// 从已归一化到 0..1 的概率中筛选标签，用于图内已含 Sigmoid 的模型。
    /// </summary>
    public static AiTagResult SelectFromProbabilities(
        IReadOnlyList<float> probabilities,
        string[] names,
        string[] categories,
        float generalThreshold = GeneralThreshold,
        float characterFloor = CharacterFloor,
        float seriesThreshold = SeriesThreshold,
        float seriesHintFloor = SeriesHintFloor) =>
        SelectCore(
            probabilities, names, categories, applySigmoid: false,
            generalThreshold, characterFloor, seriesThreshold, seriesHintFloor);

    private static IReadOnlyList<AiTagScore> SelectScoresCore(
        IReadOnlyList<float> values,
        string[] names,
        string[] categories,
        bool applySigmoid,
        float snapshotFloor)
    {
        if (values.Count != names.Length || names.Length != categories.Length)
        {
            throw new InvalidOperationException(
                $"模型输出维度 {values.Count} 与标签表 {names.Length} 不一致");
        }

        var scores = new List<AiTagScore>(256);
        for (var i = 0; i < values.Count; i++)
        {
            var name = names[i];
            if (name.Length == 0)
            {
                continue;
            }

            var score = applySigmoid ? Sigmoid(values[i]) : Math.Clamp(values[i], 0f, 1f);
            if (score >= snapshotFloor)
            {
                scores.Add(new AiTagScore(i, name, categories[i], score));
            }
        }

        if (scores.Count <= MaxSnapshotTags)
        {
            return scores;
        }

        return scores
            .OrderByDescending(entry => entry.Score)
            .Take(MaxSnapshotTags)
            .OrderBy(entry => entry.Index)
            .ToList();
    }

    private static AiTagResult SelectCore(
        IReadOnlyList<float> values,
        string[] names,
        string[] categories,
        bool applySigmoid,
        float generalThreshold,
        float characterFloor,
        float seriesThreshold,
        float seriesHintFloor)
    {
        if (values.Count != names.Length || names.Length != categories.Length)
        {
            throw new InvalidOperationException(
                $"模型输出维度 {values.Count} 与标签表 {names.Length} 不一致");
        }

        var general = new List<AiTag>(MaxGeneralTags);
        var characters = new List<AiTag>(MaxCharacterTags + 2);
        var seriesCandidates = new List<AiTag>(8);
        AiTag? bestSeries = null;

        for (var i = 0; i < values.Count; i++)
        {
            var name = names[i];
            if (name.Length == 0)
            {
                continue;
            }

            var category = categories[i];
            if (category.Length == 0)
            {
                continue;
            }

            var score = applySigmoid ? Sigmoid(values[i]) : Math.Clamp(values[i], 0f, 1f);

            switch (category)
            {
                case AiTagCategory.General:
                    if (score >= generalThreshold)
                    {
                        general.Add(new AiTag(name, category, score));
                    }

                    break;

                case AiTagCategory.Character:
                    if (score >= characterFloor)
                    {
                        characters.Add(new AiTag(name, category, score));
                    }

                    break;

                case AiTagCategory.Series:
                    if (score >= seriesHintFloor)
                    {
                        seriesCandidates.Add(new AiTag(name, category, score));
                    }

                    if (bestSeries is null || score > bestSeries.Score)
                    {
                        bestSeries = new AiTag(name, category, score);
                    }

                    break;
            }
        }

        var topCharacters = characters
            .OrderByDescending(tag => tag.Score)
            .Take(MaxCharacterTags)
            .ToList();

        var tags = new List<AiTag>(MaxGeneralTags + MaxCharacterTags + 1);

        // 角色在前。界面按顺序取第一条作为结论，其余作为候选。
        tags.AddRange(topCharacters);

        var series = ResolveSeries(topCharacters, seriesCandidates, bestSeries, seriesThreshold);
        if (series is not null)
        {
            tags.Add(series);
        }

        tags.AddRange(general
            .OrderByDescending(tag => tag.Score)
            .Take(MaxGeneralTags));

        return new AiTagResult(tags);
    }

    /// <summary>
    /// 系列头与角色结论对齐。角色标签常带 <c>_(girls'_frontline)</c> 这样的作品后缀，
    /// 它比独立的系列头可靠。有后缀但没有匹配的系列时宁可留空，也不展示别的作品。
    /// </summary>
    private static AiTag? ResolveSeries(
        IReadOnlyList<AiTag> characters,
        IReadOnlyList<AiTag> candidates,
        AiTag? best,
        float seriesThreshold)
    {
        var hint = SeriesHint(characters.Count > 0 ? characters[0].Name : null);
        if (hint is null)
        {
            return best is not null && best.Score >= seriesThreshold ? best : null;
        }

        if (SeriesAliases.TryGetValue(hint, out var alias))
        {
            hint = alias;
        }

        return candidates
            .Select(tag => (Tag: tag, Quality: SeriesMatchQuality(tag.Name, hint)))
            .Where(item => item.Quality >= 0)
            .OrderByDescending(item => item.Quality)
            .ThenByDescending(item => item.Tag.Score)
            .Select(item => item.Tag)
            .FirstOrDefault();
    }

    /// <summary>
    /// 取角色标签最后一组括号里的作品名，如 <c>ump45_(mod3)_(girls'_frontline)</c>。
    /// </summary>
    private static string? SeriesHint(string? characterName)
    {
        if (string.IsNullOrEmpty(characterName) || characterName[^1] != ')')
        {
            return null;
        }

        var start = characterName.LastIndexOf("_(", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var hint = characterName[(start + 2)..^1];
        return hint.Length == 0 ? null : hint;
    }

    /// <summary>
    /// 作品名匹配强度。精确名最高，其次是 <c>xxx_(series)</c> 与去后缀相同，
    /// 再次是如 <c>fate/grand_order</c> 的子系列，最后是如 <c>honkai_(series)</c> 的上级系列。
    /// </summary>
    private static int SeriesMatchQuality(string seriesName, string hint)
    {
        if (seriesName == hint)
        {
            return 3;
        }

        const string seriesSuffix = "_(series)";
        var baseName = seriesName.EndsWith(seriesSuffix, StringComparison.Ordinal)
            ? seriesName[..^seriesSuffix.Length]
            : seriesName;
        if (baseName == hint)
        {
            return 2;
        }

        if (seriesName.StartsWith(hint + "_", StringComparison.Ordinal)
            || seriesName.StartsWith(hint + ":", StringComparison.Ordinal)
            || seriesName.StartsWith(hint + "/", StringComparison.Ordinal))
        {
            return 1;
        }

        if (hint.StartsWith(baseName + ":", StringComparison.Ordinal)
            || hint.StartsWith(baseName + "_", StringComparison.Ordinal))
        {
            return 0;
        }

        return -1;
    }

    public static float Sigmoid(float value)
    {
        // 数值稳定的写法，避免 exp 溢出。
        if (value >= 0)
        {
            return 1f / (1f + MathF.Exp(-value));
        }

        var exp = MathF.Exp(value);
        return exp / (1f + exp);
    }
}
