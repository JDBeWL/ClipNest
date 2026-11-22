using ClipNest.Core.Models;

namespace ClipNest.App.Controls;

/// <summary>
/// 标签星系布局，按根标签分簇、货架式排列簇包络与同心轨道节点，全部为纯函数计算。
/// </summary>
public static class GalaxyLayout
{
    public const double GoldenAngle = 2.399963229728653;

    /// <summary>缩略图世界尺寸；轨道间距按它留出固定空隙，保证节点不重叠。</summary>
    public const double NodeSize = 128;

    private const double NodeSpacing = NodeSize + 12;
    private const double NodeHalf = NodeSize / 2;
    private const double ClusterPadding = 24;
    private const double MinClusterRadius = 160;
    private const double ClusterGap = 40;

    public static GalaxyLayoutResult Build(GalaxySnapshot snapshot)
    {
        if (snapshot.Assets.Count == 0)
        {
            return new GalaxyLayoutResult();
        }

        var tags = new Dictionary<long, GalaxyTag>();
        foreach (var tag in snapshot.Tags)
        {
            tags[tag.Id] = tag;
        }

        // 按根标签分组，未分类使用键 0。
        var groups = new Dictionary<long, List<GalaxyAsset>>();
        foreach (var asset in snapshot.Assets)
        {
            long? primary = null;
            var bestDepth = -1;
            foreach (var tagId in asset.TagIds)
            {
                if (!tags.ContainsKey(tagId))
                {
                    continue; // 悬空标签不参与主导标签判定。
                }

                var depth = GetDepth(tagId, tags);
                if (depth > bestDepth ||
                    (depth == bestDepth && (primary is null || tagId < primary.Value)))
                {
                    bestDepth = depth;
                    primary = tagId;
                }
            }

            var root = primary is null ? null : GetRoot(primary.Value, tags);
            var key = root ?? 0;
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<GalaxyAsset>();
                groups[key] = list;
            }

            list.Add(asset);
        }

        // 根标签按 Id 升序，未分类最后。
        var seeds = new List<ClusterSeed>();
        long nextId = 1;
        foreach (var key in groups.Keys.Where(k => k > 0).OrderBy(k => k))
        {
            seeds.Add(CreateSeed(key, nextId++, groups[key], tags));
        }

        if (groups.TryGetValue(0, out var untagged))
        {
            seeds.Add(CreateSeed(0, nextId, untagged, tags));
        }

        PackClusters(seeds);

        var clusters = seeds
            .Select(seed => new GalaxyCluster
            {
                Id = seed.Id,
                TagId = seed.Key > 0 ? seed.Key : null,
                TagPath = seed.TagPath,
                Label = seed.Label,
                Color = seed.Color,
                CenterX = seed.CenterX,
                CenterY = seed.CenterY,
                Radius = seed.Radius,
                AssetCount = seed.Assets.Count
            })
            .ToArray();

        // 簇内节点按同心轨道排布。同一环上的间距与相邻环的径向距离都留出缩略图空隙，
        // 因此节点不会叠在一起，放大后可以直接逐张点选。
        var nodes = new List<GalaxyNode>(snapshot.Assets.Count);
        foreach (var seed in seeds)
        {
            var ordered = seed.Assets
                .OrderByDescending(a => a.Asset.TakenAt.HasValue)
                .ThenByDescending(a => a.Asset.TakenAt)
                .ThenByDescending(a => a.Asset.Rating)
                .ThenBy(a => a.Asset.Id)
                .ToList();

            PlaceOnOrbits(seed, ordered, nodes);
        }

        return new GalaxyLayoutResult
        {
            Clusters = clusters,
            Nodes = nodes
        };
    }

    /// <summary>
    /// 中心放第一张，其余按环展开。每环的容量按周长除以间距取整，
    /// 环内均匀取角，相邻环错开半格，保证任何两个节点至少相隔一张缩略图。
    /// </summary>
    private static void PlaceOnOrbits(
        ClusterSeed seed, IReadOnlyList<GalaxyAsset> ordered, List<GalaxyNode> nodes)
    {
        if (ordered.Count == 0) return;

        var index = 0;
        AddNode(0, 0);
        for (var ring = 1; index < ordered.Count; ring++)
        {
            var count = Math.Min(RingCapacity(ring), ordered.Count - index);
            var radius = ring * NodeSpacing;
            var rotation = ring * GoldenAngle;
            for (var slot = 0; slot < count; slot++)
            {
                var angle = rotation + slot * (2 * Math.PI / count);
                AddNode(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            }
        }

        void AddNode(double x, double y)
        {
            var asset = ordered[index++];
            nodes.Add(new GalaxyNode
            {
                Asset = asset.Asset,
                ClusterId = seed.Id,
                X = seed.CenterX + x,
                Y = seed.CenterY + y,
                TagIds = asset.TagIds.OrderBy(id => id).ToArray()
            });
        }
    }

    private static int RingCapacity(int ring) =>
        Math.Max(1, (int)Math.Floor(2 * Math.PI * ring));

    private static int RingCount(int count)
    {
        if (count <= 1) return 0;

        var placed = 1;
        var ring = 0;
        while (placed < count)
        {
            ring++;
            placed += RingCapacity(ring);
        }

        return ring;
    }

    private static double ClusterRadius(int count) =>
        Math.Max(MinClusterRadius, NodeSpacing * RingCount(count) + NodeHalf + ClusterPadding);

    private static ClusterSeed CreateSeed(
        long key, long id, List<GalaxyAsset> assets, Dictionary<long, GalaxyTag> tags)
    {
        var rootTag = key > 0 && tags.TryGetValue(key, out var tag) ? tag : null;
        return new ClusterSeed
        {
            Key = key,
            Id = id,
            Label = rootTag?.Name ?? "未分类",
            TagPath = rootTag is null ? "" : BuildPath(rootTag.Id, tags),
            Color = rootTag?.Color,
            Radius = ClusterRadius(assets.Count),
            Assets = assets
        };
    }

    private static long? GetRoot(long tagId, Dictionary<long, GalaxyTag> tags)
    {
        var visited = new HashSet<long>();
        var current = tagId;
        while (tags.TryGetValue(current, out var tag) &&
               tag.ParentId is { } parentId &&
               tags.ContainsKey(parentId))
        {
            if (!visited.Add(current))
            {
                break; // 防御父链成环。
            }

            current = parentId;
        }

        return tags.ContainsKey(current) ? current : null;
    }

    private static int GetDepth(long tagId, Dictionary<long, GalaxyTag> tags)
    {
        var depth = 0;
        var visited = new HashSet<long>();
        var current = tagId;
        while (tags.TryGetValue(current, out var tag) &&
               tag.ParentId is { } parentId &&
               tags.ContainsKey(parentId))
        {
            if (!visited.Add(current))
            {
                break;
            }

            depth++;
            current = parentId;
        }

        return depth;
    }

    private static string BuildPath(long tagId, Dictionary<long, GalaxyTag> tags)
    {
        var names = new List<string>();
        var visited = new HashSet<long>();
        var current = tagId;
        while (tags.TryGetValue(current, out var tag))
        {
            if (!visited.Add(current))
            {
                break;
            }

            names.Add(tag.Name);
            if (tag.ParentId is not { } parentId || !tags.ContainsKey(parentId))
            {
                break;
            }

            current = parentId;
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// 大簇优先，按行铺开。每行高度取该行最大直径，行内与行间都留出固定间隙。
    /// 未分类单独停在标签块下方左侧，形成固定的「未归类」角落。
    /// </summary>
    private static void PackClusters(List<ClusterSeed> seeds)
    {
        if (seeds.Count == 0) return;

        var untagged = seeds.FirstOrDefault(seed => seed.Key == 0);
        var ordered = seeds
            .Where(seed => seed.Key != 0)
            .OrderByDescending(seed => seed.Radius)
            .ThenBy(seed => seed.Id)
            .ToList();

        var area = ordered.Sum(seed => Math.Pow(2 * seed.Radius + ClusterGap, 2));
        var rowWidth = Math.Sqrt(area) * 1.5;

        var x = 0.0;
        var y = 0.0;
        var rowHeight = 0.0;

        foreach (var seed in ordered)
        {
            var diameter = seed.Radius * 2;
            if (x > 0 && x + diameter > rowWidth)
            {
                x = 0;
                y += rowHeight + ClusterGap;
                rowHeight = 0;
            }

            seed.CenterX = x + seed.Radius;
            seed.CenterY = y + seed.Radius;
            x += diameter + ClusterGap;
            rowHeight = Math.Max(rowHeight, diameter);
        }

        if (untagged is not null)
        {
            if (ordered.Count > 0)
            {
                y += rowHeight + ClusterGap;
            }

            untagged.CenterX = untagged.Radius;
            untagged.CenterY = y + untagged.Radius;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var seed in seeds)
        {
            minX = Math.Min(minX, seed.CenterX - seed.Radius);
            minY = Math.Min(minY, seed.CenterY - seed.Radius);
            maxX = Math.Max(maxX, seed.CenterX + seed.Radius);
            maxY = Math.Max(maxY, seed.CenterY + seed.Radius);
        }

        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        foreach (var seed in seeds)
        {
            seed.CenterX -= centerX;
            seed.CenterY -= centerY;
        }
    }

    private sealed class ClusterSeed
    {
        public long Key { get; init; }
        public long Id { get; init; }
        public string Label { get; init; } = "";
        public string TagPath { get; init; } = "";
        public string? Color { get; init; }
        public double Radius { get; init; }
        public List<GalaxyAsset> Assets { get; init; } = new();
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }
}