using ClipNest.Core.Models;
using Dapper;

namespace ClipNest.Data.Repositories;

public sealed class FolderRepository
{
    private readonly ClipNestDatabase _db;

    public FolderRepository(ClipNestDatabase db) => _db = db;

    public async Task<long> EnsureAsync(string path, CancellationToken ct = default)
    {
        var normalized = Path.GetFullPath(path);
        await using var connection = await _db.OpenAsync(ct);
        var existing = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM folders WHERE path = @Path;", new { Path = normalized });
        if (existing is not null) return existing.Value;

        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO folders (path, watch_enabled, created_at)
            VALUES (@Path, 1, @CreatedAt);
            SELECT last_insert_rowid();
            """, new { Path = normalized, CreatedAt = DateTime.UtcNow });
    }

    public async Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<Folder>("""
            SELECT f.id,
                   f.path,
                   f.watch_enabled,
                   f.tag_model_id,
                   f.last_scan_at,
                   (SELECT COUNT(*)
                    FROM asset_files af
                    WHERE af.folder_id = f.id AND af.file_state = 'indexed') AS asset_count
            FROM folders f
            ORDER BY f.path;
            """);
        return rows.ToList();
    }

    /// <summary>指定文件夹的标注模型；空字符串表示跟随默认模型。</summary>
    public async Task SetTagModelAsync(long folderId, string modelId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE folders SET tag_model_id = @ModelId WHERE id = @Id;",
            new { Id = folderId, ModelId = modelId });
    }

    public async Task MarkScannedAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE folders SET last_scan_at = @Now WHERE id = @Id;",
            new { Id = folderId, Now = DateTime.UtcNow });
    }

    /// <summary>
    /// 只从图库移除文件夹，不触动磁盘上的文件。
    /// </summary>
    public async Task RemoveAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            "DELETE FROM asset_files WHERE folder_id = @Id;",
            new { Id = folderId }, transaction);

        await connection.ExecuteAsync("""
            DELETE FROM assets
            WHERE NOT EXISTS (SELECT 1 FROM asset_files af WHERE af.asset_id = assets.id);
            """, transaction: transaction);

        await connection.ExecuteAsync(
            "DELETE FROM folders WHERE id = @Id;",
            new { Id = folderId }, transaction);

        await transaction.CommitAsync(ct);
    }
}
