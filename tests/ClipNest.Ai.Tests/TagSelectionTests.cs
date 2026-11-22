using ClipNest.Ai.Tagging;
using ClipNest.Core.Tagging;

namespace ClipNest.Ai.Tests;

/// <summary>
/// 标签筛选是这套识别能力里唯一有产品判断的部分，决定哪些分可信、哪些仅作候选。
/// </summary>
public sealed class TagSelectionTests
{
    /// <summary>把概率转换为 logit，便于直接描述期望的 0.6 分。</summary>
    private static float Logit(float probability)
        => MathF.Log(probability / (1 - probability));

    private static AiTagResult Select(
        (string Name, string Category, float Probability)[] entries)
    {
        var logits = entries.Select(entry => Logit(entry.Probability)).ToArray();
        var names = entries.Select(entry => entry.Name).ToArray();
        var categories = entries.Select(entry => entry.Category).ToArray();
        return TagSelection.Select(logits, names, categories);
    }

    [Fact]
    public void Select_KeepsCharacterCandidatesSortedByScore()
    {
        var result = Select(new[]
        {
            ("tsukimura_temari", AiTagCategory.Character, 0.29f),
            ("fujita_kotone", AiTagCategory.Character, 0.22f),
            ("long_hair", AiTagCategory.General, 0.71f)
        });

        var characters = result.Tags.Where(tag => tag.IsCharacter).ToList();
        Assert.Equal(2, characters.Count);
        Assert.Equal("tsukimura_temari", characters[0].Name);
        Assert.Equal("fujita_kotone", characters[1].Name);
    }

    [Fact]
    public void Select_DropsCharacterBelowFloor()
    {
        var result = Select(new[]
        {
            ("oyashio_(kancolle)", AiTagCategory.Character, 0.12f)
        });

        Assert.Null(result.Character);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void Select_CapsCharacterCandidates()
    {
        var result = Select(new[]
        {
            ("a", AiTagCategory.Character, 0.50f),
            ("b", AiTagCategory.Character, 0.40f),
            ("c", AiTagCategory.Character, 0.30f),
            ("d", AiTagCategory.Character, 0.25f),
            ("e", AiTagCategory.Character, 0.20f)
        });

        Assert.Equal(TagSelection.MaxCharacterTags, result.Tags.Count(tag => tag.IsCharacter));
    }

    [Fact]
    public void Select_KeepsOnlyGeneralTagsAboveThreshold()
    {
        var result = Select(new[]
        {
            ("long_hair", AiTagCategory.General, 0.71f),
            ("blurry", AiTagCategory.General, 0.20f)
        });

        var features = result.FeatureTags.ToList();
        Assert.Single(features);
        Assert.Equal("long_hair", features[0].Name);
    }

    [Fact]
    public void Select_KeepsBestSeriesOnlyWhenConfidentEnough()
    {
        var confident = Select(new[]
        {
            ("gakuen_idolmaster", AiTagCategory.Series, 0.70f),
            ("idolmaster", AiTagCategory.Series, 0.48f)
        });
        Assert.Equal("gakuen_idolmaster", confident.Series?.Name);

        var weak = Select(new[]
        {
            ("original", AiTagCategory.Series, 0.20f)
        });
        Assert.Null(weak.Series);
    }

    [Fact]
    public void Select_OrdersCharacterThenSeriesThenFeatures()
    {
        var result = Select(new[]
        {
            ("long_hair", AiTagCategory.General, 0.90f),
            ("gakuen_idolmaster", AiTagCategory.Series, 0.60f),
            ("fujita_kotone", AiTagCategory.Character, 0.30f)
        });

        Assert.Equal(
            new[] { AiTagCategory.Character, AiTagCategory.Series, AiTagCategory.General },
            result.Tags.Select(tag => tag.Category).ToArray());
    }

    [Fact]
    public void Select_IgnoresUnknownAndEmptyNames()
    {
        var result = TagSelection.Select(
            new[] { 5f, 5f, 5f },
            new[] { "", "nijisanji", "artist_style" },
            new[] { AiTagCategory.General, AiTagCategory.Series, AiTagCategory.Artist });

        Assert.Single(result.Tags);
        Assert.Equal("nijisanji", result.Tags[0].Name);
        Assert.DoesNotContain(result.Tags, tag => tag.Category == AiTagCategory.Artist);
    }

    [Fact]
    public void Select_RejectsMismatchedDimensions()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TagSelection.Select(new[] { 1f, 2f }, new[] { "a" }, new[] { AiTagCategory.General }));
    }

    [Fact]
    public void SelectScores_KeepsTagsAboveFloor_InIndexOrder()
    {
        var scores = TagSelection.SelectScores(
            new[]
            {
                Logit(0.12f),
                Logit(0.05f),
                Logit(0.60f),
                Logit(0.35f)
            },
            new[] { "low", "noise", "", "fujita_kotone" },
            new[]
            {
                AiTagCategory.General,
                AiTagCategory.General,
                AiTagCategory.General,
                AiTagCategory.Character
            });

        Assert.Equal(new[] { 0, 3 }, scores.Select(entry => entry.Index).ToArray());
        Assert.Equal("fujita_kotone", scores[1].Name);
        Assert.Equal(AiTagCategory.Character, scores[1].Category);
        Assert.InRange(scores[1].Score, 0.34f, 0.36f);
    }

    [Fact]
    public void SelectScores_CapsByScore_ThenRestoresIndexOrder()
    {
        var count = TagSelection.MaxSnapshotTags + 20;
        var logits = new float[count];
        var names = new string[count];
        var categories = new string[count];
        for (var i = 0; i < count; i++)
        {
            // 下标越大分数越高，截断后应保留后半段。
            logits[i] = Logit(0.11f + i * 0.001f);
            names[i] = $"tag_{i}";
            categories[i] = AiTagCategory.General;
        }

        var scores = TagSelection.SelectScores(logits, names, categories);

        Assert.Equal(TagSelection.MaxSnapshotTags, scores.Count);
        Assert.Equal(count - TagSelection.MaxSnapshotTags, scores[0].Index);
        Assert.Equal(count - 1, scores[^1].Index);
        Assert.True(scores.Zip(scores.Skip(1)).All(pair => pair.First.Index < pair.Second.Index));
    }

    [Fact]
    public void SelectScores_RejectsMismatchedDimensions()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TagSelection.SelectScores(
                new[] { 1f, 2f }, new[] { "a" }, new[] { AiTagCategory.General }));
    }

    [Fact]
    public void Select_PrefersSeriesImpliedByCharacterSuffix()
    {
        var result = Select(new[]
        {
            ("ump45_(girls'_frontline)", AiTagCategory.Character, 0.32f),
            ("honkai_(series)", AiTagCategory.Series, 0.61f),
            ("girls'_frontline", AiTagCategory.Series, 0.18f)
        });

        Assert.Equal("girls'_frontline", result.Series?.Name);
    }

    [Fact]
    public void Select_HidesUnrelatedSeries_WhenCharacterSuffixHasNoMatch()
    {
        var result = Select(new[]
        {
            ("ump45_(girls'_frontline)", AiTagCategory.Character, 0.32f),
            ("honkai_(series)", AiTagCategory.Series, 0.61f)
        });

        Assert.Null(result.Series);
    }

    [Fact]
    public void Select_MapsKancolleToKantaiCollection()
    {
        var result = Select(new[]
        {
            ("oyashio_(kancolle)", AiTagCategory.Character, 0.19f),
            ("nijisanji", AiTagCategory.Series, 0.37f),
            ("kantai_collection", AiTagCategory.Series, 0.12f)
        });

        Assert.Equal("kantai_collection", result.Series?.Name);
    }

    [Fact]
    public void Select_PrefersExactSubSeriesOverParent()
    {
        var result = Select(new[]
        {
            ("trailblazer_(honkai:_star_rail)", AiTagCategory.Character, 0.30f),
            ("honkai_(series)", AiTagCategory.Series, 0.62f),
            ("honkai:_star_rail", AiTagCategory.Series, 0.20f)
        });

        Assert.Equal("honkai:_star_rail", result.Series?.Name);
    }

    [Fact]
    public void DisplayName_ReplacesUnderscores()
    {
        Assert.Equal("long hair", new AiTag("long_hair", AiTagCategory.General, 0.5f).DisplayName);
    }
}
