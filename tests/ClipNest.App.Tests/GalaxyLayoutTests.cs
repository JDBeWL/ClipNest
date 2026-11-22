using ClipNest.App.Controls;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class GalaxyLayoutTests
{
    [Fact]
    public void Layout_PlacesUntaggedIntoOwnCluster()
    {
        var snapshot = new GalaxySnapshot
        {
            Assets = new[]
            {
                new GalaxyAsset
                {
                    Asset = new AssetListItem { Id = 1, Sha256 = "a", FileName = "a.jpg" },
                    TagIds = Array.Empty<long>()
                },
                new GalaxyAsset
                {
                    Asset = new AssetListItem { Id = 2, Sha256 = "b", FileName = "b.jpg" },
                    TagIds = Array.Empty<long>()
                }
            }
        };

        var result = GalaxyLayout.Build(snapshot);

        var cluster = Assert.Single(result.Clusters);
        Assert.Null(cluster.TagId);
        Assert.Equal("未分类", cluster.Label);
        Assert.Equal("", cluster.TagPath);
        Assert.Equal(2, cluster.AssetCount);
        Assert.All(result.Nodes, node => Assert.Equal(cluster.Id, node.ClusterId));
    }

    [Fact]
    public void Layout_IsDeterministic_AndWithinRadius()
    {
        var snapshot = new GalaxySnapshot
        {
            Tags = new[]
            {
                new GalaxyTag { Id = 1, Name = "旅行" },
                new GalaxyTag { Id = 2, Name = "夜景", ParentId = 1 }
            },
            Assets = Enumerable.Range(1, 30).Select(i => new GalaxyAsset
            {
                Asset = new AssetListItem { Id = i, Sha256 = $"h{i}", FileName = $"{i}.jpg", Rating = i % 5 },
                TagIds = i % 3 == 0 ? new long[] { 1 } : new long[] { 1, 2 }
            }).ToArray()
        };

        var first = GalaxyLayout.Build(snapshot);
        var second = GalaxyLayout.Build(snapshot);

        Assert.Equal(first.Nodes.Select(n => (n.Asset.Id, n.X, n.Y)),
                     second.Nodes.Select(n => (n.Asset.Id, n.X, n.Y)));
        Assert.Single(first.Clusters);
        Assert.Equal("旅行", first.Clusters[0].Label);
        Assert.All(first.Nodes, node =>
        {
            var cluster = first.Clusters.Single(c => c.Id == node.ClusterId);
            var distance = Math.Sqrt(Math.Pow(node.X - cluster.CenterX, 2) + Math.Pow(node.Y - cluster.CenterY, 2));
            Assert.True(distance <= cluster.Radius + 0.001);
        });
    }

    [Fact]
    public void Layout_LargeCluster_IsDeterministic()
    {
        var snapshot = new GalaxySnapshot
        {
            Assets = Enumerable.Range(1, 600).Select(i => new GalaxyAsset
            {
                Asset = new AssetListItem { Id = i, Sha256 = $"h{i}", FileName = $"{i}.jpg" },
                TagIds = Array.Empty<long>()
            }).ToArray()
        };

        var first = GalaxyLayout.Build(snapshot);
        var second = GalaxyLayout.Build(snapshot);

        Assert.Equal(600, first.Nodes.Count);
        Assert.Equal(
            first.Nodes.Select(n => (n.Asset.Id, n.X, n.Y)),
            second.Nodes.Select(n => (n.Asset.Id, n.X, n.Y)));
        Assert.Equal(600, first.Nodes.Select(n => (n.X, n.Y)).Distinct().Count());
    }

    [Fact]
    public void Layout_CreatesClusterPerRootTag_AndUnclassifiedLast()
    {
        var snapshot = new GalaxySnapshot
        {
            // 刻意乱序，验证簇按根标签 Id 升序而非输入顺序排列。
            Tags = new[]
            {
                new GalaxyTag { Id = 2, Name = "夜景" },
                new GalaxyTag { Id = 1, Name = "旅行" },
                new GalaxyTag { Id = 3, Name = "人像", ParentId = 2 }
            },
            Assets = new[]
            {
                Asset(1, 2),
                Asset(2, 1),
                Asset(3),
                Asset(4, 3)
            }
        };

        var result = GalaxyLayout.Build(snapshot);

        Assert.Equal(3, result.Clusters.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, result.Clusters.Select(c => c.Id));
        Assert.Equal(new long?[] { 1, 2, null }, result.Clusters.Select(c => c.TagId));
        Assert.Equal(new[] { "旅行", "夜景", "未分类" }, result.Clusters.Select(c => c.Label));
        Assert.Equal(new[] { 1, 2, 1 }, result.Clusters.Select(c => c.AssetCount));

        // 子标签资产归入根簇。
        var childTagNode = Assert.Single(result.Nodes, n => n.Asset.Id == 4);
        Assert.Equal(2, childTagNode.ClusterId);
        Assert.All(result.Nodes.Where(n => n.Asset.Id == 3), n => Assert.Equal(3, n.ClusterId));
    }

    [Fact]
    public void Layout_IgnoresCoOccurrenceEdgesWithoutCluster()
    {
        var snapshot = new GalaxySnapshot
        {
            // 每张图都带多条平级标签，但只有最低 id 的标签成为主导簇；
            // 共现边引用了没有簇的标签时不能中断整个布局。
            Tags = new[]
            {
                new GalaxyTag { Id = 1, Name = "a" },
                new GalaxyTag { Id = 2, Name = "b" },
                new GalaxyTag { Id = 3, Name = "c" }
            },
            Assets = new[]
            {
                Asset(1, 1, 2),
                Asset(2, 1, 3)
            }
        };

        var result = GalaxyLayout.Build(snapshot);

        Assert.Single(result.Clusters);
        Assert.Equal(2, result.Nodes.Count);
    }

    private static GalaxyAsset Asset(long id, params long[] tagIds) => new()
    {
        Asset = new AssetListItem { Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg" },
        TagIds = tagIds
    };
}
