using ClipNest.Core.Models;
using Dapper;

namespace ClipNest.Data.Search;

public sealed class SearchRepository
{
    private readonly ClipNestDatabase _db;

    public SearchRepository(ClipNestDatabase db) => _db = db;

    public async Task<IReadOnlyList<AssetListItem>> QueryAsync(
        CompiledSearch compiled, int limit, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        compiled.Parameters.Add("limit", limit);
        var rows = await connection.QueryAsync<AssetListItem>(compiled.Sql, compiled.Parameters);
        return rows.ToList();
    }

    /// <summary>
    /// 按传入 ID 顺序批量回表；空集合直接返回空。
    /// </summary>
    public async Task<IReadOnlyList<AssetListItem>> GetByIdsAsync(
        IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<AssetListItem>();

        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AssetListItem>("""
            SELECT a.id, a.sha256, a.file_name,
                   COALESCE(af.path, '') AS primary_path,
                   a.width, a.height, a.taken_at, a.created_at,
                   a.rating, a.is_favorite, a.decode_status
            FROM assets a
            LEFT JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            WHERE a.id IN @Ids;
            """, new { Ids = ids });

        var byId = rows.ToDictionary(row => row.Id);
        var result = new List<AssetListItem>(ids.Count);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var item)) result.Add(item);
        }

        return result;
    }
}
