using ClipNest.Core.Models;
using Dapper;

namespace ClipNest.Data.Repositories;

public sealed class TagRepository
{
    private readonly ClipNestDatabase _db;

    public TagRepository(ClipNestDatabase db) => _db = db;

    public async Task<long> CreateAsync(string name, long? parentId, string? color = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO tags (name, normalized_name, parent_id, color, created_at)
            VALUES (@Name, @Normalized, @ParentId, @Color, @CreatedAt);
            SELECT last_insert_rowid();
            """, new
        {
            Name = name.Trim(),
            Normalized = TagPath.Normalize(name),
            ParentId = parentId,
            Color = color,
            CreatedAt = DateTime.UtcNow
        });
    }

    public async Task<long> GetOrCreateAsync(
        string name, long? parentId = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var existing = await connection.ExecuteScalarAsync<long?>("""
            SELECT id FROM tags
            WHERE COALESCE(parent_id, 0) = COALESCE(@ParentId, 0)
              AND normalized_name = @Normalized;
            """, new { ParentId = parentId, Normalized = TagPath.Normalize(name) });
        return existing ?? await CreateAsync(name, parentId, null, ct);
    }

    public async Task<IReadOnlyList<Tag>> GetTreeAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var tags = (await connection.QueryAsync<Tag>(
            "SELECT id, name, normalized_name, parent_id, color FROM tags ORDER BY name;")).ToList();
        var byParent = tags.GroupBy(t => t.ParentId ?? 0).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var tag in tags)
        {
            tag.Children = byParent.GetValueOrDefault(tag.Id) ?? new List<Tag>();
        }
        return byParent.GetValueOrDefault(0) ?? new List<Tag>();
    }

    /// <summary>
    /// 删除不再被任何图片引用的标签。自叶向根逐层清理，父标签只要还有子标签或图片引用就保留。
    /// 返回删除的标签数。
    /// </summary>
    /// <remarks>
    /// 文件夹移除、图片被删时，asset_tags 会随外键级联清空，但 tags 里会留下没有归属的名字。
    /// 标签树不过滤空标签，所以必须显式清理，否则侧栏会一直显示点开没有任何图片的条目。
    /// </remarks>
    public async Task<int> PruneUnusedAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var removed = 0;
        while (true)
        {
            var count = await connection.ExecuteAsync("""
                DELETE FROM tags
                WHERE NOT EXISTS (SELECT 1 FROM asset_tags at WHERE at.tag_id = tags.id)
                  AND NOT EXISTS (SELECT 1 FROM tags child WHERE child.parent_id = tags.id);
                """);
            removed += count;
            if (count == 0) return removed;
        }
    }

    public async Task<IReadOnlyList<long>> GetTagIdsByPathAsync(
        string path, bool includeDescendants, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var tags = (await connection.QueryAsync<Tag>(
            "SELECT id, name, normalized_name, parent_id, color FROM tags;")).ToList();
        var byParent = tags.GroupBy(t => t.ParentId ?? 0).ToDictionary(g => g.Key, g => g.ToList());

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TagPath.Normalize).ToArray();
        if (segments.Length == 0) return Array.Empty<long>();

        var current = byParent.GetValueOrDefault(0) ?? new List<Tag>();
        Tag? match = null;
        foreach (var segment in segments)
        {
            match = current.FirstOrDefault(t => t.NormalizedName == segment);
            if (match is null) return Array.Empty<long>();
            current = byParent.GetValueOrDefault(match.Id) ?? new List<Tag>();
        }

        if (match is null) return Array.Empty<long>();
        if (!includeDescendants) return new[] { match.Id };

        var result = new List<long>();
        void Walk(Tag tag)
        {
            result.Add(tag.Id);
            foreach (var child in byParent.GetValueOrDefault(tag.Id) ?? new List<Tag>())
                Walk(child);
        }
        Walk(match);
        return result;
    }

    public async Task AssignAsync(long assetId, long tagId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO asset_tags (asset_id, tag_id, source) VALUES (@AssetId, @TagId, 'manual')
            ON CONFLICT(asset_id, tag_id) DO UPDATE SET source = 'manual', model_id = NULL;
            """, new { AssetId = assetId, TagId = tagId });
    }

    public async Task RemoveAsync(long assetId, long tagId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "DELETE FROM asset_tags WHERE asset_id = @AssetId AND tag_id = @TagId;",
            new { AssetId = assetId, TagId = tagId });
    }

    public async Task<IReadOnlyList<Tag>> GetTagsForAssetAsync(long assetId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<Tag>("""
            SELECT t.id, t.name, t.normalized_name, t.parent_id, t.color
            FROM tags t JOIN asset_tags at ON at.tag_id = t.id
            WHERE at.asset_id = @AssetId ORDER BY t.name;
            """, new { AssetId = assetId });
        return rows.ToList();
    }

    /// <summary>读取某张图下指定类别的标签，供详情栏单独展示角色或作品。</summary>
    public async Task<IReadOnlyList<Tag>> GetTagsByCategoryAsync(
        long assetId, string category, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<Tag>("""
            SELECT t.id, t.name, t.normalized_name, t.parent_id, t.color
            FROM tags t JOIN asset_tags at ON at.tag_id = t.id
            WHERE at.asset_id = @AssetId AND at.category = @Category
            ORDER BY t.name;
            """, new { AssetId = assetId, Category = category });
        return rows.ToList();
    }

    /// <summary>
    /// 用一组名字替换指定类别的标签。手动编辑写 source=manual，
    /// 之后 AI 重新识别时会跳过这个类别，用户改过的值不会被覆盖。
    /// </summary>
    public async Task ReplaceCategoryTagsAsync(
        IReadOnlyList<long> assetIds,
        string category,
        IReadOnlyList<string> names,
        CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return;

        var unique = names
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var assetId in assetIds)
        {
            await connection.ExecuteAsync("""
                DELETE FROM asset_tags
                WHERE asset_id = @AssetId AND category = @Category;
                """, new { AssetId = assetId, Category = category }, transaction);

            foreach (var name in unique)
            {
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
                    INSERT INTO asset_tags (asset_id, tag_id, source, category)
                    SELECT @AssetId, id, 'manual', @Category
                    FROM tags
                    WHERE COALESCE(parent_id, 0) = 0 AND normalized_name = @Normalized
                    ON CONFLICT(asset_id, tag_id) DO UPDATE SET
                        source = 'manual',
                        category = @Category,
                        model_id = NULL;
                    """, new
                {
                    AssetId = assetId,
                    Normalized = normalized,
                    Category = category
                }, transaction);
            }

            // 即使名字列表为空也要留下覆盖标记。用户清空角色或作品后，AI 重识别不应再填回来。
            await connection.ExecuteAsync("""
                INSERT INTO asset_category_overrides (asset_id, category, updated_at)
                VALUES (@AssetId, @Category, @UpdatedAt)
                ON CONFLICT(asset_id, category) DO UPDATE SET updated_at = excluded.updated_at;
                """, new
            {
                AssetId = assetId,
                Category = category,
                UpdatedAt = DateTime.UtcNow
            }, transaction);
        }

        await transaction.CommitAsync(ct);
    }
}
