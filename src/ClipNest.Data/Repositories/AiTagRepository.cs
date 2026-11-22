using ClipNest.Core.Models;
using ClipNest.Core.Tagging;
using Dapper;

namespace ClipNest.Data.Repositories;

/// <summary>
/// 待识别的资产任务。
/// </summary>
public sealed record AiTagJob(long AssetId, string Path, string FileName);

/// <summary>
/// 识别覆盖率统计。
/// </summary>
public sealed record AiTagStats(int Pending, int Done);

/// <summary>
/// AI 标签持久化。原始结论保留在 asset_ai_tags 与分数快照中，可据此重建；
/// 角色、作品与特征结论同步进 tags 与 asset_tags，详情栏可像手写标签一样增删。
/// </summary>
public sealed class AiTagRepository
{
    private readonly ClipNestDatabase _db;

    public AiTagRepository(ClipNestDatabase db) => _db = db;

    public async Task ReplaceAsync(
        long assetId,
        string modelId,
        AiTagResult result,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // 结论与快照整体替换。重跑一张图时旧分数必须一起清除，否则调整阈值会读到过期数据。
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tags WHERE asset_id = @AssetId AND model_id = @ModelId;",
            new { AssetId = assetId, ModelId = modelId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tag_scores WHERE asset_id = @AssetId AND model_id = @ModelId;",
            new { AssetId = assetId, ModelId = modelId }, transaction);

        if (result.Tags.Count > 0)
        {
            await connection.ExecuteAsync("""
                INSERT INTO asset_ai_tags (asset_id, tag, category, score, model_id, created_at)
                VALUES (@AssetId, @Tag, @Category, @Score, @ModelId, @CreatedAt);
                """, result.Tags.Select(tag => new
            {
                AssetId = assetId,
                Tag = tag.Name,
                Category = tag.Category,
                Score = (double)tag.Score,
                ModelId = modelId,
                CreatedAt = DateTime.UtcNow
            }), transaction);
        }

        // 用户手动改过的角色与作品优先。这两个类别一旦被编辑，就不再被 AI 结论覆盖。
        // 覆盖标记独立成表，用户清空角色或作品这种没有标签可留的情况也能记住。
        var manualCategories = (await connection.QueryAsync<string>("""
            SELECT category FROM asset_category_overrides WHERE asset_id = @AssetId;
            """, new { AssetId = assetId }, transaction))
            .ToHashSet(StringComparer.Ordinal);

        // 识别结论直接进入标签体系，角色、作品与画面特征都可以像手写标签一样增删。
        // 只清理这张图此前的 AI 关联，用户手动维护的 manual 关联保持不动。
        await connection.ExecuteAsync(
            "DELETE FROM asset_tags WHERE asset_id = @AssetId AND source = 'ai';",
            new { AssetId = assetId }, transaction);

        foreach (var tag in result.Tags)
        {
            if (manualCategories.Contains(tag.Category))
            {
                continue;
            }

            var name = tag.DisplayName;
            var normalized = TagPath.Normalize(name);
            await connection.ExecuteAsync("""
                INSERT OR IGNORE INTO tags (name, normalized_name, parent_id, color, created_at)
                VALUES (@Name, @Normalized, NULL, NULL, @CreatedAt);
                """, new
            {
                Name = name,
                Normalized = normalized,
                CreatedAt = DateTime.UtcNow
            }, transaction);

            await connection.ExecuteAsync("""
                INSERT OR IGNORE INTO asset_tags (asset_id, tag_id, source, confidence, category, model_id)
                SELECT @AssetId, id, 'ai', @Confidence, @Category, @ModelId
                FROM tags
                WHERE COALESCE(parent_id, 0) = 0 AND normalized_name = @Normalized;
                """, new
            {
                AssetId = assetId,
                Normalized = normalized,
                Confidence = (double)tag.Score,
                Category = tag.Category,
                ModelId = modelId
            }, transaction);
        }

        if (result.Scores.Count > 0)
        {
            // 词表按模型只保留一份。同一模型重复标注时用 upsert 跟上元数据变化。
            await connection.ExecuteAsync("""
                INSERT INTO ai_tag_catalog (model_id, tag_index, name, category)
                VALUES (@ModelId, @TagIndex, @Name, @Category)
                ON CONFLICT(model_id, tag_index) DO UPDATE SET
                    name = excluded.name,
                    category = excluded.category;
                """, result.Scores.Select(score => new
            {
                ModelId = modelId,
                TagIndex = score.Index,
                Name = score.Name,
                Category = score.Category
            }), transaction);

            await connection.ExecuteAsync("""
                INSERT INTO asset_ai_tag_scores (asset_id, model_id, tag_index, score)
                VALUES (@AssetId, @ModelId, @TagIndex, @Score);
                """, result.Scores.Select(score => new
            {
                AssetId = assetId,
                ModelId = modelId,
                TagIndex = score.Index,
                Score = (double)score.Score
            }), transaction);
        }

        // 完成标记独立于标签条数。整张图都低于阈值时也要记为已处理，避免队列反复重跑。
        // 版本号让筛选规则升级后旧结果自动失效。
        await connection.ExecuteAsync("""
            INSERT INTO asset_ai_tag_runs (asset_id, model_id, tagged_at, pipeline_version)
            VALUES (@AssetId, @ModelId, @TaggedAt, @PipelineVersion)
            ON CONFLICT(asset_id, model_id) DO UPDATE SET
                tagged_at = excluded.tagged_at,
                pipeline_version = excluded.pipeline_version;
            """, new
        {
            AssetId = assetId,
            ModelId = modelId,
            TaggedAt = DateTime.UtcNow,
            PipelineVersion = TaggingPipeline.Version
        }, transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 清空指定模型的识别结果与完成标记，让队列重新处理该模型负责的资产。
    /// 角色与作品的结论会按模型重建，手动添加的其他标签保留。
    /// </summary>
    public async Task ResetAsync(string modelId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var assetFilter = """
            SELECT af.asset_id
            FROM asset_files af
            JOIN folders f ON f.id = af.folder_id
            WHERE af.is_primary = 1
              AND COALESCE(NULLIF(f.tag_model_id, ''), @ModelId) = @ModelId
            """;
        var parameters = new { ModelId = modelId };

        await connection.ExecuteAsync(
            $"DELETE FROM asset_category_overrides WHERE asset_id IN ({assetFilter});",
            parameters, transaction);
        await connection.ExecuteAsync($"""
            DELETE FROM asset_tags
            WHERE source = 'manual'
              AND category IN ('character', 'copyright')
              AND asset_id IN ({assetFilter});
            """, parameters, transaction);
        await connection.ExecuteAsync($"""
            DELETE FROM asset_tags
            WHERE source = 'ai'
              AND asset_id IN ({assetFilter});
            """, parameters, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tags WHERE model_id = @ModelId;",
            new { ModelId = modelId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tag_scores WHERE model_id = @ModelId;",
            new { ModelId = modelId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tag_runs WHERE model_id = @ModelId;",
            new { ModelId = modelId }, transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 清空单张图片的识别结果与完成标记，让队列按所属文件夹的模型重新处理。
    /// 角色与作品的结论会按模型重建，手动添加的其他标签保留。
    /// </summary>
    public async Task ResetForAssetAsync(long assetId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            "DELETE FROM asset_category_overrides WHERE asset_id = @AssetId;",
            new { AssetId = assetId }, transaction);
        await connection.ExecuteAsync("""
            DELETE FROM asset_tags
            WHERE asset_id = @AssetId
              AND source = 'manual'
              AND category IN ('character', 'copyright');
            """, new { AssetId = assetId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_tags WHERE asset_id = @AssetId AND source = 'ai';",
            new { AssetId = assetId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tags WHERE asset_id = @AssetId;",
            new { AssetId = assetId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tag_scores WHERE asset_id = @AssetId;",
            new { AssetId = assetId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_ai_tag_runs WHERE asset_id = @AssetId;",
            new { AssetId = assetId }, transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 读取图片所属文件夹指定的模型；未指定时返回默认模型。
    /// </summary>
    public async Task<string?> GetModelIdForAssetAsync(
        long assetId, string defaultModelId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string?>("""
            SELECT COALESCE(NULLIF(f.tag_model_id, ''), @DefaultModelId)
            FROM asset_files af
            JOIN folders f ON f.id = af.folder_id
            WHERE af.asset_id = @AssetId AND af.is_primary = 1
            LIMIT 1;
            """, new { AssetId = assetId, DefaultModelId = defaultModelId });
    }

    /// <summary>
    /// 清空指定文件夹的识别结果与完成标记，让队列按该文件夹的模型重新处理。
    /// 角色与作品的结论会按模型重建，手动添加的其他标签保留。
    /// </summary>
    public async Task ResetForFolderAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var assetFilter = """
            SELECT af.asset_id
            FROM asset_files af
            WHERE af.folder_id = @FolderId
            """;
        var parameters = new { FolderId = folderId };

        await connection.ExecuteAsync(
            $"DELETE FROM asset_category_overrides WHERE asset_id IN ({assetFilter});",
            parameters, transaction);
        await connection.ExecuteAsync($"""
            DELETE FROM asset_tags
            WHERE source = 'manual'
              AND category IN ('character', 'copyright')
              AND asset_id IN ({assetFilter});
            """, parameters, transaction);
        await connection.ExecuteAsync(
            $"DELETE FROM asset_tags WHERE source = 'ai' AND asset_id IN ({assetFilter});",
            parameters, transaction);
        await connection.ExecuteAsync(
            $"DELETE FROM asset_ai_tags WHERE asset_id IN ({assetFilter});",
            parameters, transaction);
        await connection.ExecuteAsync(
            $"DELETE FROM asset_ai_tag_scores WHERE asset_id IN ({assetFilter});",
            parameters, transaction);
        await connection.ExecuteAsync(
            $"DELETE FROM asset_ai_tag_runs WHERE asset_id IN ({assetFilter});",
            parameters, transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 清空全部模型的识别结果与完成标记，让队列按各文件夹的模型重新处理。
    /// 角色与作品的结论会按模型重建，手动添加的其他标签保留。
    /// </summary>
    public async Task ResetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync("DELETE FROM asset_category_overrides;", transaction);
        await connection.ExecuteAsync("""
            DELETE FROM asset_tags
            WHERE source = 'manual' AND category IN ('character', 'copyright');
            """, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM asset_tags WHERE source = 'ai';", transaction);
        await connection.ExecuteAsync("DELETE FROM asset_ai_tags;", transaction);
        await connection.ExecuteAsync("DELETE FROM asset_ai_tag_scores;", transaction);
        await connection.ExecuteAsync("DELETE FROM asset_ai_tag_runs;", transaction);

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 读取当前流水线版本的识别结果。规则升级后、重标完成前旧结果不再返回，
    /// 避免界面继续展示已经确认错误的作品标签。
    /// </summary>
    public async Task<IReadOnlyList<AiTag>> GetForAssetAsync(
        long assetId,
        string modelId,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AiTagRow>("""
            SELECT t.tag AS Name, t.category AS Category, t.score AS Score
            FROM asset_ai_tags t
            WHERE t.asset_id = @AssetId AND t.model_id = @ModelId
              AND EXISTS (
                SELECT 1 FROM asset_ai_tag_runs r
                WHERE r.asset_id = t.asset_id
                  AND r.model_id = t.model_id
                  AND r.pipeline_version = @PipelineVersion
              )
            ORDER BY CASE t.category WHEN 'character' THEN 0 WHEN 'copyright' THEN 1 ELSE 2 END,
                     t.score DESC;
            """, new
        {
            AssetId = assetId,
            ModelId = modelId,
            PipelineVersion = TaggingPipeline.Version
        });

        return rows.Select(row => new AiTag(row.Name, row.Category, (float)row.Score)).ToList();
    }

    /// <summary>
    /// 还没有识别结果的资产，按 id 顺序取一批。
    /// </summary>
    /// <remarks>
    /// 以完成标记而非标签条数判断，整张图都低于阈值的资产也算处理过；只有 asset_ai_tags
    /// 而没有分数快照的资产会重新入队补齐，旧版本标记在规则升级后同样失效。
    /// 磁盘上已经不存在的文件不再入队，它们永远识别不成功，只会在每次轮询里空转。
    /// </remarks>
    public Task<IReadOnlyList<AiTagJob>> GetPendingAsync(
        string modelId,
        int limit,
        long afterAssetId = 0,
        CancellationToken ct = default)
        => GetPendingAsync(modelId, modelId, limit, afterAssetId, ct);

    public async Task<IReadOnlyList<AiTagJob>> GetPendingAsync(
        string modelId,
        string defaultModelId,
        int limit,
        long afterAssetId = 0,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AiTagJob>("""
            SELECT a.id AS AssetId, af.path AS Path, af.file_name AS FileName
            FROM assets a
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            JOIN folders f ON f.id = af.folder_id
            LEFT JOIN asset_ai_tag_runs r
              ON r.asset_id = a.id AND r.model_id = @ModelId AND r.pipeline_version = @PipelineVersion
            WHERE r.asset_id IS NULL
              AND af.file_state = 'indexed'
              AND COALESCE(NULLIF(f.tag_model_id, ''), @DefaultModelId) = @ModelId
              AND a.id > @AfterAssetId
            ORDER BY a.id
            LIMIT @Limit;
            """, new
        {
            ModelId = modelId,
            DefaultModelId = defaultModelId,
            PipelineVersion = TaggingPipeline.Version,
            AfterAssetId = afterAssetId,
            Limit = limit
        });

        return rows.ToList();
    }

    /// <summary>
    /// 统计各文件夹按各自模型识别后的覆盖率；未指定模型的文件夹使用默认模型。
    /// </summary>
    public async Task<AiTagStats> GetStatsAsync(string defaultModelId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);

        // 分母只算有可读主文件的资产。被删掉的图片仍留在库里保留历史，
        // 但它们不可能再识别，算进分母会让进度永远到不了总数。
        var total = await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM assets a
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            JOIN folders f ON f.id = af.folder_id
            WHERE af.file_state = 'indexed';
            """);

        var done = await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM asset_ai_tag_runs r
            JOIN assets a ON a.id = r.asset_id
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            JOIN folders f ON f.id = af.folder_id
            WHERE af.file_state = 'indexed'
              AND r.model_id = COALESCE(NULLIF(f.tag_model_id, ''), @DefaultModelId)
              AND r.pipeline_version = @PipelineVersion;
            """,
            new { DefaultModelId = defaultModelId, PipelineVersion = TaggingPipeline.Version });

        return new AiTagStats(Math.Max(0, total - done), Math.Min(done, total));
    }

    private sealed class AiTagRow
    {
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public double Score { get; init; }
    }
}
