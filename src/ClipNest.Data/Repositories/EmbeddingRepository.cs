using Dapper;

namespace ClipNest.Data.Repositories;

/// <summary>
/// 待生成嵌入的资产任务。
/// </summary>
public sealed record EmbeddingJob(long AssetId, string Sha256, string Path, string FileName, string Extension);

/// <summary>
/// 嵌入状态统计。
/// </summary>
public sealed record EmbeddingStats(int Pending, int Ready, int Error, int Skipped);

/// <summary>
/// 嵌入向量持久化，向量以 float16 小端字节存于 BLOB。
/// </summary>
public sealed class EmbeddingRepository
{
    private readonly ClipNestDatabase _db;

    public EmbeddingRepository(ClipNestDatabase db) => _db = db;

    public async Task UpsertAsync(long assetId, string modelId, float[] vector, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync("""
            INSERT INTO asset_embeddings (asset_id, model_id, dim, vector, created_at)
            VALUES (@AssetId, @ModelId, @Dim, @Vector, @CreatedAt)
            ON CONFLICT(asset_id, model_id) DO UPDATE SET
              dim = excluded.dim,
              vector = excluded.vector,
              created_at = excluded.created_at;
            """, new
        {
            AssetId = assetId,
            ModelId = modelId,
            Dim = vector.Length,
            Vector = Encode(vector),
            CreatedAt = now
        }, transaction);

        await connection.ExecuteAsync(
            "UPDATE assets SET embedding_status = 'ready', updated_at = @Now WHERE id = @AssetId;",
            new { Now = now, AssetId = assetId }, transaction);

        await transaction.CommitAsync(ct);
    }

    public async Task<Dictionary<long, float[]>> GetAllVectorsAsync(string modelId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<EmbeddingRow>(
            "SELECT asset_id AS AssetId, dim AS Dim, vector AS Vector FROM asset_embeddings WHERE model_id = @ModelId;",
            new { ModelId = modelId });

        var result = new Dictionary<long, float[]>();
        foreach (var row in rows)
        {
            result[row.AssetId] = Decode(row.Vector, (int)row.Dim);
        }

        return result;
    }

    public async Task<IReadOnlyList<EmbeddingJob>> GetPendingAsync(int limit = 8, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<EmbeddingJob>("""
            SELECT a.id AS AssetId,
                   a.sha256 AS Sha256,
                   af.path AS Path,
                   af.file_name AS FileName,
                   af.extension AS Extension
            FROM assets a
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            WHERE a.embedding_status = 'pending' AND af.file_state = 'indexed'
            ORDER BY a.id
            LIMIT @Limit;
            """, new { Limit = limit });
        return rows.ToList();
    }

    public async Task MarkStatusAsync(long assetId, string status, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE assets SET embedding_status = @Status, updated_at = @Now WHERE id = @AssetId;",
            new { Status = status, Now = DateTime.UtcNow, AssetId = assetId });
    }

    public async Task<int> MarkStatusAsync(string from, string to, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await connection.ExecuteAsync(
            "UPDATE assets SET embedding_status = @To, updated_at = @Now WHERE embedding_status = @From;",
            new { To = to, From = from, Now = DateTime.UtcNow });
    }

    public async Task<EmbeddingStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);

        // 用单行聚合而不是 GROUP BY；空库时 GROUP BY 返回 0 行，
        // Dapper 在这种 reader 上无法物化 record，列类型退化成 BLOB。
        // 分母只算有可读主文件的资产，把已删除的图片算进去会让进度永远到不了总数。
        var row = await connection.QuerySingleAsync<StatusTotals>("""
            SELECT COALESCE(SUM(CASE WHEN a.embedding_status = 'pending' THEN 1 ELSE 0 END), 0) AS Pending,
                   COALESCE(SUM(CASE WHEN a.embedding_status = 'ready' THEN 1 ELSE 0 END), 0) AS Ready,
                   COALESCE(SUM(CASE WHEN a.embedding_status = 'error' THEN 1 ELSE 0 END), 0) AS Error,
                   COALESCE(SUM(CASE WHEN a.embedding_status = 'skipped' THEN 1 ELSE 0 END), 0) AS Skipped
            FROM assets a
            WHERE EXISTS (
                SELECT 1 FROM asset_files af
                WHERE af.asset_id = a.id AND af.is_primary = 1 AND af.file_state = 'indexed');
            """);

        return new EmbeddingStats(
            (int)row.Pending,
            (int)row.Ready,
            (int)row.Error,
            (int)row.Skipped);
    }

    /// <summary>
    /// 编码为 float16 小端字节。
    /// </summary>
    private static byte[] Encode(float[] vector)
    {
        var bytes = new byte[vector.Length * 2];
        for (var i = 0; i < vector.Length; i++)
        {
            var bits = BitConverter.HalfToInt16Bits((Half)vector[i]);
            bytes[i * 2] = (byte)bits;
            bytes[i * 2 + 1] = (byte)(bits >> 8);
        }

        return bytes;
    }

    /// <summary>
    /// 解码 float16 小端字节。
    /// </summary>
    private static float[] Decode(byte[] bytes, int dim)
    {
        var vector = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            vector[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        }

        return vector;
    }

    private sealed record EmbeddingRow(long AssetId, long Dim, byte[] Vector);

    private sealed record StatusTotals(long Pending, long Ready, long Error, long Skipped);
}
