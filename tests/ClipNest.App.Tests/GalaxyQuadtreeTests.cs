using Avalonia;
using ClipNest.App.Controls;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class GalaxyQuadtreeTests
{
    [Fact]
    public void Query_ReturnsNodesInsideBounds()
    {
        var nodes = new[]
        {
            Node(1, 0, 0), Node(2, 100, 0), Node(3, 1000, 1000), Node(4, 50, 50)
        };
        var tree = GalaxyQuadtree.Build(nodes);

        var hits = tree.Query(new Rect(-10, -10, 200, 200));

        Assert.Equal(new long[] { 1, 2, 4 }, hits.Select(n => n.Asset.Id).OrderBy(id => id));
        Assert.Equal(4, tree.HitTest(new Point(52, 52), 10)!.Asset.Id);
        Assert.Null(tree.HitTest(new Point(500, 500), 10));
    }

    [Fact]
    public void EmptyTree_QueryAndHitTestAreSafe()
    {
        var tree = GalaxyQuadtree.Build(Array.Empty<GalaxyNode>());

        Assert.Empty(tree.Query(new Rect(0, 0, 100, 100)));
        Assert.Null(tree.HitTest(new Point(0, 0), 10));
    }

    private static GalaxyNode Node(long id, double x, double y) => new()
    {
        Asset = new AssetListItem { Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg" },
        X = x,
        Y = y
    };
}
