namespace ClipNest.Core.Models;

public sealed class GalaxyAsset
{
    public AssetListItem Asset { get; init; } = new();
    public IReadOnlyList<long> TagIds { get; init; } = Array.Empty<long>();
}

public sealed class GalaxyTag
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long? ParentId { get; init; }
    public string? Color { get; init; }
}

public sealed class GalaxySnapshot
{
    public IReadOnlyList<GalaxyAsset> Assets { get; init; } = Array.Empty<GalaxyAsset>();
    public IReadOnlyList<GalaxyTag> Tags { get; init; } = Array.Empty<GalaxyTag>();

    /// <summary>资产 Id 到嵌入向量的映射；为空表示未按模型读取嵌入。</summary>
    public IReadOnlyDictionary<long, float[]> Embeddings { get; init; } = new Dictionary<long, float[]>();
}

public sealed class GalaxyCluster
{
    public long Id { get; init; }              // 1..n，未分类最后。
    public long? TagId { get; init; }          // 未分类为 null。
    public string TagPath { get; init; } = ""; // 供 #标签 搜索。
    public string Label { get; init; } = "";
    public string? Color { get; init; }
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    public double Radius { get; init; }
    public int AssetCount { get; init; }
}

public sealed class GalaxyNode
{
    public AssetListItem Asset { get; init; } = new();
    public long ClusterId { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public IReadOnlyList<long> TagIds { get; init; } = Array.Empty<long>();
}

public sealed class GalaxyLayoutResult
{
    public IReadOnlyList<GalaxyCluster> Clusters { get; init; } = Array.Empty<GalaxyCluster>();
    public IReadOnlyList<GalaxyNode> Nodes { get; init; } = Array.Empty<GalaxyNode>();
}
