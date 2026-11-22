using ClipNest.App.Controls;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class GalaxyPackingTests
{
    private const long TagId = 1;

    [Fact]
    public void Layout_PacksNodesOnOrbits_WithoutOverlap()
    {
        var snapshot = CreateSnapshot(40, new Dictionary<long, float[]>
        {
            [1] = new[] { 1f, 0f },
            [2] = new[] { 1f, 0f }
        });

        var result = GalaxyLayout.Build(snapshot);

        var cluster = Assert.Single(result.Clusters);
        Assert.Equal(40, result.Nodes.Count);

        // 最新的一张放在簇心，其余按环向外展开。
        var center = Assert.Single(result.Nodes, n => n.Asset.Id == 1);
        Assert.Equal(cluster.CenterX, center.X, 9);
        Assert.Equal(cluster.CenterY, center.Y, 9);

        // 任意两张缩略图之间至少留出一整张缩略图的间距。
        for (var i = 0; i < result.Nodes.Count; i++)
        {
            for (var j = i + 1; j < result.Nodes.Count; j++)
            {
                var a = result.Nodes[i];
                var b = result.Nodes[j];
                var distance = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                Assert.True(distance >= GalaxyLayout.NodeSize,
                    $"节点 {a.Asset.Id} 与 {b.Asset.Id} 相距 {distance}，小于缩略图尺寸");
            }
        }

        Assert.All(result.Nodes, node =>
        {
            var distance = Math.Sqrt(
                Math.Pow(node.X - cluster.CenterX, 2) + Math.Pow(node.Y - cluster.CenterY, 2));
            Assert.True(distance <= cluster.Radius + 0.001);
        });
    }

    [Fact]
    public void Layout_PinsUntaggedClusterBelowTaggedBlock()
    {
        var snapshot = new GalaxySnapshot
        {
            Tags = new[] { new GalaxyTag { Id = TagId, Name = "旅行" } },
            Assets = Enumerable.Range(1, 20)
                .Select(id => new GalaxyAsset
                {
                    Asset = new AssetListItem { Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg" },
                    TagIds = id <= 15 ? new[] { TagId } : Array.Empty<long>()
                })
                .ToArray()
        };

        var result = GalaxyLayout.Build(snapshot);

        var tagged = Assert.Single(result.Clusters, c => c.TagId == TagId);
        var untagged = Assert.Single(result.Clusters, c => c.TagId is null);
        Assert.True(
            untagged.CenterY >= tagged.CenterY + tagged.Radius + untagged.Radius,
            $"未分类簇应停在标签块下方，实际 y={untagged.CenterY}");
    }

    [Fact]
    public void Layout_IgnoresEmbeddings()
    {
        var without = GalaxyLayout.Build(CreateSnapshot(12, null));
        var with = GalaxyLayout.Build(CreateSnapshot(12, new Dictionary<long, float[]>
        {
            [1] = new[] { 1f, 0f },
            [2] = new[] { 0f, 1f },
            [3] = new[] { 0.5f, 0.5f }
        }));

        Assert.Equal(
            without.Nodes.Select(n => (n.Asset.Id, n.X, n.Y)),
            with.Nodes.Select(n => (n.Asset.Id, n.X, n.Y)));
    }

    private static GalaxySnapshot CreateSnapshot(
        int count, IReadOnlyDictionary<long, float[]>? embeddings) => new()
    {
        Tags = new[] { new GalaxyTag { Id = TagId, Name = "旅行" } },
        Assets = Enumerable.Range(1, count)
            .Select(id => new GalaxyAsset
            {
                Asset = new AssetListItem { Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg" },
                TagIds = new[] { TagId }
            })
            .ToArray(),
        Embeddings = embeddings ?? new Dictionary<long, float[]>()
    };
}