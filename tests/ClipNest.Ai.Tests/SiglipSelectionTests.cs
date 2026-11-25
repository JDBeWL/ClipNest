using ClipNest.Ai.Tagging;
using ClipNest.Core.Tagging;

namespace ClipNest.Ai.Tests;

/// <summary>
/// SigLIP2 用余弦阈值筛选标签；这里固定阈值、排序与快照的行为。
/// </summary>
public sealed class SiglipSelectionTests
{
    [Fact]
    public void Select_KeepsTagsAboveCosineFloor_RankedByScore()
    {
        var tags = new[] { "bee", "flower", "building", "empty" };
        var cosines = new[] { 0.12f, 0.10f, 0.03f, 0f };

        var result = SiglipSelection.Select(cosines, tags);

        Assert.Equal(new[] { "bee", "flower" }, result.Tags.Select(tag => tag.Name));
        Assert.True(result.Tags[0].Score > result.Tags[1].Score);
        Assert.All(result.Tags, tag => Assert.Equal(AiTagCategory.General, tag.Category));
    }

    [Fact]
    public void Select_SnapshotKeepsWeakMatchesAboveFloor_InIndexOrder()
    {
        var tags = new[] { "bee", "flower", "building", "sky" };
        var cosines = new[] { 0.12f, 0.10f, 0.06f, 0.01f };

        var result = SiglipSelection.Select(cosines, tags);

        Assert.Equal(new[] { 0, 1, 2 }, result.Scores.Select(row => row.Index));
        Assert.Equal(new[] { "bee", "flower" }, result.Tags.Select(tag => tag.Name));
    }

    [Fact]
    public void Select_RejectsMismatchedDimensions()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SiglipSelection.Select(new[] { 1f, 2f }, new[] { "a" }));
    }

    [Fact]
    public void Select_CapsConclusionsAtGeneralTagLimit()
    {
        var tags = Enumerable.Range(0, TagSelection.MaxGeneralTags + 5)
            .Select(index => $"tag_{index}")
            .ToArray();
        var cosines = tags.Select(_ => 0.12f).ToArray();

        var result = SiglipSelection.Select(cosines, tags);

        Assert.Equal(TagSelection.MaxGeneralTags, result.Tags.Count);
    }
}
