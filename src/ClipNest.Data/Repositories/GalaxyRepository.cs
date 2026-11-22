using ClipNest.Core.Models;
using Dapper;

namespace ClipNest.Data.Repositories;

public sealed class GalaxyRepository
{
    private readonly ClipNestDatabase _db;

    public GalaxyRepository(ClipNestDatabase db) => _db = db;

    /// <summary>
    /// 读取星系快照，只包含主文件仍在磁盘上的资产，按拍摄时间倒序，缺失时用创建时间。
    /// 默认不设条数上限；maxAssets 仅用于测试等需要截断的场景。
    /// embeddingModelId 非空时同时读取这批资产在该模型下的嵌入向量。
    /// </summary>
    public async Task<GalaxySnapshot> GetSnapshotAsync(
        int maxAssets = int.MaxValue, string? embeddingModelId = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);

        var assets = (await connection.QueryAsync<AssetListItem>("""
            SELECT a.id, a.sha256, a.file_name, a.width, a.height, a.taken_at, a.created_at,
                   a.rating, a.is_favorite, a.decode_status, af.path AS primary_path
            FROM assets a INDEXED BY ix_assets_galaxy_sort
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            WHERE af.file_state = 'indexed'
            ORDER BY COALESCE(a.taken_at, a.created_at) DESC, a.id DESC
            LIMIT @MaxAssets;
            """, new { MaxAssets = maxAssets })).ToList();

        var links = (await connection.QueryAsync<AssetTagLink>("""
            SELECT at.asset_id, at.tag_id
            FROM asset_tags at
            WHERE at.asset_id IN (
              SELECT a.id FROM assets a INDEXED BY ix_assets_galaxy_sort
              JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
              WHERE af.file_state = 'indexed'
              ORDER BY COALESCE(a.taken_at, a.created_at) DESC, a.id DESC
              LIMIT @MaxAssets
            );
            """, new { MaxAssets = maxAssets })).ToList();

        var tags = await connection.QueryAsync<GalaxyTag>(
            "SELECT id, name, parent_id, color FROM tags;");

        var tagIdsByAsset = links
            .GroupBy(link => link.AssetId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<long>)group.Select(link => link.TagId).OrderBy(id => id).ToArray());

        var galaxyAssets = assets
            .Select(asset => new GalaxyAsset
            {
                Asset = asset,
                TagIds = tagIdsByAsset.GetValueOrDefault(asset.Id) ?? Array.Empty<long>()
            })
            .ToList();

        var embeddings = new Dictionary<long, float[]>();
        if (!string.IsNullOrEmpty(embeddingModelId))
        {
            var rows = await connection.QueryAsync<EmbeddingRow>("""
                SELECT ae.asset_id AS AssetId, ae.dim AS Dim, ae.vector AS Vector
                FROM asset_embeddings ae
                WHERE ae.model_id = @ModelId AND ae.asset_id IN (
                  SELECT a.id FROM assets a INDEXED BY ix_assets_galaxy_sort
                  JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
                  WHERE af.file_state = 'indexed'
                  ORDER BY COALESCE(a.taken_at, a.created_at) DESC, a.id DESC
                  LIMIT @MaxAssets
                );
                """, new { ModelId = embeddingModelId, MaxAssets = maxAssets });

            foreach (var row in rows)
            {
                embeddings[row.AssetId] = DecodeVector(row.Vector, (int)row.Dim);
            }
        }

        return new GalaxySnapshot
        {
            Assets = galaxyAssets,
            Tags = tags.ToList(),
            Embeddings = embeddings
        };
    }

    /// <summary>解码 float16 小端字节为向量。</summary>
    private static float[] DecodeVector(byte[] bytes, int dim)
    {
        var vector = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            vector[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        }

        return vector;
    }

    private sealed class AssetTagLink
    {
        public long AssetId { get; init; }
        public long TagId { get; init; }
    }

    private sealed class EmbeddingRow
    {
        public long AssetId { get; init; }
        public long Dim { get; init; }
        public byte[] Vector { get; init; } = Array.Empty<byte>();
    }
}
