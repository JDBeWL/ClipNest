using Avalonia;
using ClipNest.Core.Models;

namespace ClipNest.App.Controls;

/// <summary>
/// 星系节点四叉树，用于视口裁剪与命中测试。
/// </summary>
public sealed class GalaxyQuadtree
{
    private const int Capacity = 8;
    private const int MaxDepth = 10;

    private readonly QuadNode _root;
    private readonly bool _isEmpty;

    private GalaxyQuadtree(QuadNode root, bool isEmpty)
    {
        _root = root;
        _isEmpty = isEmpty;
    }

    public static GalaxyQuadtree Build(IReadOnlyList<GalaxyNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return new GalaxyQuadtree(new QuadNode(new Rect(0, 0, 0, 0), 0), true);
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var node in nodes)
        {
            minX = Math.Min(minX, node.X);
            minY = Math.Min(minY, node.Y);
            maxX = Math.Max(maxX, node.X);
            maxY = Math.Max(maxY, node.Y);
        }

        // 包围盒外扩 1，避免节点恰好落在边界上。
        var root = new QuadNode(
            new Rect(minX - 1, minY - 1, Math.Max(maxX - minX, 0) + 2, Math.Max(maxY - minY, 0) + 2),
            0);

        foreach (var node in nodes)
        {
            Insert(root, node);
        }

        return new GalaxyQuadtree(root, false);
    }

    public IReadOnlyList<GalaxyNode> Query(Rect bounds)
    {
        if (_isEmpty)
        {
            return Array.Empty<GalaxyNode>();
        }

        var result = new List<GalaxyNode>();
        Query(_root, bounds, result);
        return result;
    }

    public GalaxyNode? HitTest(Point point, double radius)
    {
        if (_isEmpty || radius < 0)
        {
            return null;
        }

        var candidates = Query(new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2));
        GalaxyNode? best = null;
        var bestDistance = double.MaxValue;
        foreach (var node in candidates)
        {
            var dx = node.X - point.X;
            var dy = node.Y - point.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= radius && distance < bestDistance)
            {
                bestDistance = distance;
                best = node;
            }
        }

        return best;
    }

    private static void Insert(QuadNode node, GalaxyNode item)
    {
        var point = new Point(item.X, item.Y);
        if (node.Children is not null)
        {
            var child = FindChild(node, point);
            if (child is not null)
            {
                Insert(child, item);
                return;
            }

            node.Items.Add(item);
            return;
        }

        node.Items.Add(item);
        if (node.Items.Count <= Capacity || node.Depth >= MaxDepth)
        {
            return;
        }

        Subdivide(node);
    }

    private static void Subdivide(QuadNode node)
    {
        var halfWidth = node.Bounds.Width / 2;
        var halfHeight = node.Bounds.Height / 2;
        var x = node.Bounds.X;
        var y = node.Bounds.Y;
        node.Children = new[]
        {
            new QuadNode(new Rect(x, y, halfWidth, halfHeight), node.Depth + 1),
            new QuadNode(new Rect(x + halfWidth, y, halfWidth, halfHeight), node.Depth + 1),
            new QuadNode(new Rect(x, y + halfHeight, halfWidth, halfHeight), node.Depth + 1),
            new QuadNode(new Rect(x + halfWidth, y + halfHeight, halfWidth, halfHeight), node.Depth + 1)
        };

        var pending = node.Items.ToArray();
        node.Items.Clear();
        foreach (var item in pending)
        {
            Insert(node, item);
        }
    }

    private static QuadNode? FindChild(QuadNode node, Point point)
    {
        if (node.Children is null)
        {
            return null;
        }

        foreach (var child in node.Children)
        {
            if (child.Bounds.Contains(point))
            {
                return child;
            }
        }

        return null;
    }

    private static void Query(QuadNode node, Rect bounds, List<GalaxyNode> result)
    {
        if (!node.Bounds.Intersects(bounds))
        {
            return;
        }

        foreach (var item in node.Items)
        {
            if (bounds.Contains(new Point(item.X, item.Y)))
            {
                result.Add(item);
            }
        }

        if (node.Children is null)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Query(child, bounds, result);
        }
    }

    private sealed class QuadNode
    {
        public QuadNode(Rect bounds, int depth)
        {
            Bounds = bounds;
            Depth = depth;
        }

        public Rect Bounds { get; }
        public int Depth { get; }
        public List<GalaxyNode> Items { get; } = new();
        public QuadNode[]? Children { get; set; }
    }
}
