using Dapper;

namespace ClipNest.Data.Repositories;

/// <summary>
/// 一个角色名下的图片归属。Source 为 user 时是用户确认，auto 是原型自动归类。
/// Series 是用户角色名里带出的作品，用于覆盖模型的作品判断。
/// </summary>
public sealed record CharacterMatch(
    long CharacterId,
    string CharacterName,
    string Source,
    double Confidence,
    string? Series);

/// <summary>
/// 角色原型，即该角色所有确认样本的特征中心。
/// </summary>
public sealed record CharacterPrototype(long CharacterId, string CharacterName, float[] Vector, int SampleCount);

/// <summary>
/// 角色、归属与原型。
/// </summary>
public sealed class CharacterRepository
{
    private readonly ClipNestDatabase _db;

    public CharacterRepository(ClipNestDatabase db) => _db = db;

    public static string Normalize(string name)
        => string.Join(' ', name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// 从角色名末尾的括号提取作品，如 <c>ump45 (girls' frontline)</c>。
    /// 用户按 AI 标签写法输入时同样适用，下划线在展示时替换为空格。
    /// </summary>
    public static string? SeriesFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var value = name.Trim();
        if (value[^1] != ')')
        {
            return null;
        }

        var start = value.LastIndexOf('(');
        if (start < 0 || start == value.Length - 2)
        {
            return null;
        }

        var series = value[(start + 1)..^1].Trim();
        return series.Length == 0 ? null : series.Replace('_', ' ');
    }

    /// <summary>按名字取角色，不存在则创建。</summary>
    public async Task<long> EnsureAsync(string name, CancellationToken ct = default)
    {
        var normalized = Normalize(name);
        var series = SeriesFromName(name);
        var now = DateTime.UtcNow;

        await using var connection = await _db.OpenAsync(ct);
        var existing = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM characters WHERE normalized_name = @Normalized;",
            new { Normalized = normalized });
        if (existing is { } id)
        {
            // 已有角色可能创建时没有作品信息，补齐时一并回写。
            if (series is not null)
            {
                await connection.ExecuteAsync("""
                    UPDATE characters SET series = @Series, updated_at = @Now
                    WHERE id = @Id AND (series IS NULL OR series = '');
                    """, new { Series = series, Id = id, Now = now });
            }

            return id;
        }

        await connection.ExecuteAsync("""
            INSERT INTO characters (name, normalized_name, series, created_at, updated_at)
            VALUES (@Name, @Normalized, @Series, @Now, @Now);
            """, new { Name = name.Trim(), Normalized = normalized, Series = series, Now = now });

        return await connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();");
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var names = await connection.QueryAsync<string>("SELECT name FROM characters ORDER BY name;");
        return names.ToList();
    }

    /// <summary>
    /// 用户确认时写入 user 归属，并清除这些图片上的其他归属，含自动归类。
    /// </summary>
    public async Task AssignUserAsync(
        long characterId,
        IReadOnlyList<long> assetIds,
        CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return;

        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            "DELETE FROM character_assets WHERE asset_id IN @AssetIds;",
            new { AssetIds = assetIds }, transaction);

        await connection.ExecuteAsync("""
            INSERT INTO character_assets (character_id, asset_id, source, confidence, created_at)
            VALUES (@CharacterId, @AssetId, 'user', 1.0, @Now);
            """, assetIds.Select(assetId => new
        {
            CharacterId = characterId,
            AssetId = assetId,
            Now = DateTime.UtcNow
        }), transaction);

        await connection.ExecuteAsync(
            "UPDATE characters SET updated_at = @Now WHERE id = @CharacterId;",
            new { Now = DateTime.UtcNow, CharacterId = characterId }, transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>清掉某个角色的自动归类，用户确认的保留。</summary>
    public async Task ClearAutoAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM character_assets WHERE character_id = @CharacterId AND source = 'auto';",
            new { CharacterId = characterId });
    }

    public async Task AssignAutoAsync(
        long characterId,
        IReadOnlyList<(long AssetId, float Score)> matches,
        CancellationToken ct = default)
    {
        if (matches.Count == 0) return;

        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // 只补空位。已经由用户确认给其他角色的图片保持不变。
        await connection.ExecuteAsync("""
            INSERT OR IGNORE INTO character_assets (character_id, asset_id, source, confidence, created_at)
            SELECT @CharacterId, @AssetId, 'auto', @Score, @Now
            WHERE NOT EXISTS (SELECT 1 FROM character_assets WHERE asset_id = @AssetId AND source = 'user');
            """, matches.Select(match => new
        {
            CharacterId = characterId,
            match.AssetId,
            Score = (double)match.Score,
            Now = DateTime.UtcNow
        }), transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>批量读取归属。同一张图有多条归属时，用户确认优先。</summary>
    public async Task<IReadOnlyDictionary<long, CharacterMatch>> GetMatchesAsync(
        IReadOnlyList<long> assetIds,
        CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return new Dictionary<long, CharacterMatch>();

        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(
            long AssetId, long CharacterId, string Name, string Source, double Confidence, string? Series)>("""
            SELECT ca.asset_id AS AssetId, ca.character_id AS CharacterId, c.name AS Name,
                   ca.source AS Source, COALESCE(ca.confidence, 1.0) AS Confidence, c.series AS Series
            FROM character_assets ca
            JOIN characters c ON c.id = ca.character_id
            WHERE ca.asset_id IN @AssetIds
            ORDER BY CASE ca.source WHEN 'user' THEN 0 ELSE 1 END, ca.confidence DESC;
            """, new { AssetIds = assetIds });

        var result = new Dictionary<long, CharacterMatch>();
        foreach (var row in rows)
        {
            // 旧数据缺少 series 列值，从角色名的括号中补齐。
            var series = string.IsNullOrWhiteSpace(row.Series)
                ? SeriesFromName(row.Name)
                : row.Series;
            result.TryAdd(row.AssetId, new CharacterMatch(
                row.CharacterId, row.Name, row.Source, row.Confidence, series));
        }

        return result;
    }

    /// <summary>清除这些图片的全部角色归属，回到模型结论。</summary>
    public async Task RemoveAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return;

        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM character_assets WHERE asset_id IN @AssetIds;",
            new { AssetIds = assetIds });
    }

    /// <summary>删掉已经没有任何图片的角色，避免空壳堆积。</summary>
    public async Task<int> PruneEmptyAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await connection.ExecuteAsync(@"
            DELETE FROM characters
            WHERE id NOT IN (SELECT DISTINCT character_id FROM character_assets);
            ");
    }

    public async Task<IReadOnlyList<long>> GetAssetIdsAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var ids = await connection.QueryAsync<long>(
            "SELECT asset_id FROM character_assets WHERE character_id = @CharacterId;",
            new { CharacterId = characterId });
        return ids.ToList();
    }

    /// <summary>某个角色名下已计算身份向量的样本向量。</summary>
    public async Task<IReadOnlyList<float[]>> GetSampleVectorsAsync(
        long characterId,
        string modelId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(int Dim, byte[] Vector)>("""
            SELECT e.dim AS Dim, e.vector AS Vector
            FROM character_assets ca
            JOIN asset_embeddings e ON e.asset_id = ca.asset_id AND e.model_id = @ModelId
            WHERE ca.character_id = @CharacterId;
            """, new { CharacterId = characterId, ModelId = modelId });

        return rows.Select(row => VectorCodec.Decode(row.Vector, row.Dim)).ToList();
    }

    public async Task SavePrototypeAsync(
        long characterId,
        string modelId,
        float[] vector,
        int sampleCount,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO character_prototypes (character_id, model_id, dim, sample_count, vector, updated_at)
            VALUES (@CharacterId, @ModelId, @Dim, @SampleCount, @Vector, @Now)
            ON CONFLICT(character_id) DO UPDATE SET
              model_id = excluded.model_id,
              dim = excluded.dim,
              sample_count = excluded.sample_count,
              vector = excluded.vector,
              updated_at = excluded.updated_at;
            """, new
        {
            CharacterId = characterId,
            ModelId = modelId,
            Dim = vector.Length,
            SampleCount = sampleCount,
            Vector = VectorCodec.Encode(vector),
            Now = DateTime.UtcNow
        });
    }

    public async Task<IReadOnlyList<CharacterPrototype>> GetPrototypesAsync(
        string modelId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long CharacterId, string Name, int Dim, int SampleCount, byte[] Vector)>("""
            SELECT p.character_id AS CharacterId, c.name AS Name, p.dim AS Dim,
                   p.sample_count AS SampleCount, p.vector AS Vector
            FROM character_prototypes p
            JOIN characters c ON c.id = p.character_id
            WHERE p.model_id = @ModelId;
            """, new { ModelId = modelId });

        return rows
            .Select(row => new CharacterPrototype(
                row.CharacterId, row.Name, VectorCodec.Decode(row.Vector, row.Dim), row.SampleCount))
            .ToList();
    }

    /// <summary>缺身份的资产，供后台补齐。</summary>
    public async Task<IReadOnlyList<(long AssetId, string Path)>> GetAssetsWithoutVectorAsync(
        string modelId,
        int limit,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long AssetId, string Path)>("""
            SELECT a.id AS AssetId, af.path AS Path
            FROM assets a
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            LEFT JOIN asset_embeddings e ON e.asset_id = a.id AND e.model_id = @ModelId
            WHERE e.asset_id IS NULL
            ORDER BY a.id
            LIMIT @Limit;
            """, new { ModelId = modelId, Limit = limit });
        return rows.ToList();
    }

    public async Task SaveVectorAsync(
        long assetId,
        string modelId,
        float[] vector,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO asset_embeddings (asset_id, model_id, dim, vector, created_at)
            VALUES (@AssetId, @ModelId, @Dim, @Vector, @Now)
            ON CONFLICT(asset_id, model_id) DO UPDATE SET
              dim = excluded.dim, vector = excluded.vector, created_at = excluded.created_at;
            """, new
        {
            AssetId = assetId,
            ModelId = modelId,
            Dim = vector.Length,
            Vector = VectorCodec.Encode(vector),
            Now = DateTime.UtcNow
        });
    }

    public async Task<IReadOnlyDictionary<long, float[]>> GetAllVectorsAsync(
        string modelId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<(long AssetId, int Dim, byte[] Vector)>(
            "SELECT asset_id AS AssetId, dim AS Dim, vector AS Vector FROM asset_embeddings WHERE model_id = @ModelId;",
            new { ModelId = modelId });

        return rows.ToDictionary(row => row.AssetId, row => VectorCodec.Decode(row.Vector, row.Dim));
    }
}
