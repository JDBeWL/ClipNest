using ClipNest.Core.Models;

namespace ClipNest.Core.Tests;

public sealed class ModelTests
{
    [Fact]
    public void Asset_Defaults_ArePending()
    {
        var asset = new Asset();

        Assert.Equal(DecodeStatus.Pending, asset.DecodeStatus);
        Assert.Equal(EmbeddingStatus.Pending, asset.EmbeddingStatus);
        Assert.Equal(0, asset.Rating);
        Assert.False(asset.IsFavorite);
    }

    [Fact]
    public void TagPath_Normalize_TrimsAndLowercases()
    {
        Assert.Equal("旅行", TagPath.Normalize(" 旅行 "));
        Assert.Equal("new york", TagPath.Normalize("New York"));
    }
}
