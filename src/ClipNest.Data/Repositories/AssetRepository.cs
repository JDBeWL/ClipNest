using System.Data;
using ClipNest.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClipNest.Data.Repositories;

/// <summary>补录宽高时需要的资产坐标。</summary>
public sealed record AssetPath(long AssetId, string Path);

/// <summary>一次搬迁要回写的文件坐标。路径、文件夹与文件名可能同时变化。</summary>
public sealed record FileRelocation(
    long FileId,
    long AssetId,
    long FolderId,
    string Path,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTime ModifiedAt);

/// <summary>复制到另一文件夹后新增的文件行。内容相同，因此复用同一个资产。</summary>
public sealed record FileAddition(
    long AssetId,
    long FolderId,
    string Path,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTime ModifiedAt);

public sealed class AssetRepository
{
    // SQLite 的绑定参数上限是 32766，批量操作按 400 一段查询，避免一次选中整库时溢出。
    private const int IdChunkSize = 400;

    private readonly ClipNestDatabase _db;

    public AssetRepository(ClipNestDatabase db) => _db = db;

    public async Task<long> UpsertAsync(Asset asset, AssetFile file, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var assetId = await UpsertCoreAsync(connection, transaction, asset, file);
        await transaction.CommitAsync(ct);
        return assetId;
    }

    /// <summary>同一连接与事务内批量写入，避免扫描大文件夹时逐文件开连接。</summary>
    public async Task UpsertManyAsync(
        IReadOnlyList<(Asset Asset, AssetFile File)> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;

        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (asset, file) in items)
            await UpsertCoreAsync(connection, transaction, asset, file);
        await transaction.CommitAsync(ct);
    }

    private static async Task<long> UpsertCoreAsync(
        SqliteConnection connection, IDbTransaction transaction, Asset asset, AssetFile file)
    {
        var assetId = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM assets WHERE sha256 = @Sha256;",
            new { asset.Sha256 }, transaction);

        if (assetId is null)
        {
            assetId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO assets
                  (sha256, file_name, width, height, orientation, taken_at, camera_make, camera_model,
                   lens, iso, aperture, shutter, focal_length, rating, is_favorite, flag,
                   decode_status, embedding_status, notes, ocr_text, created_at, updated_at)
                VALUES
                  (@Sha256, @FileName, @Width, @Height, @Orientation, @TakenAt, @CameraMake, @CameraModel,
                   @Lens, @Iso, @Aperture, @Shutter, @FocalLength, @Rating, @IsFavorite, @Flag,
                   @DecodeStatus, @EmbeddingStatus, @Notes, @OcrText, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """, new
            {
                asset.Sha256, asset.FileName, asset.Width, asset.Height, asset.Orientation,
                asset.TakenAt, asset.CameraMake, asset.CameraModel, asset.Lens, asset.Iso,
                asset.Aperture, asset.Shutter, asset.FocalLength, asset.Rating,
                asset.IsFavorite, asset.Flag,
                DecodeStatus = asset.DecodeStatus.ToString().ToLowerInvariant(),
                EmbeddingStatus = asset.EmbeddingStatus.ToString().ToLowerInvariant(),
                asset.Notes, asset.OcrText, asset.CreatedAt, asset.UpdatedAt
            }, transaction);
        }
        else
        {
            await connection.ExecuteAsync("""
                UPDATE assets SET
                  file_name = @FileName, width = @Width, height = @Height, orientation = @Orientation,
                  taken_at = @TakenAt, camera_make = @CameraMake, camera_model = @CameraModel,
                  lens = @Lens, iso = @Iso, aperture = @Aperture, shutter = @Shutter,
                  focal_length = @FocalLength, updated_at = @UpdatedAt
                WHERE id = @AssetId;
                """, new
            {
                AssetId = assetId.Value,
                asset.FileName, asset.Width, asset.Height, asset.Orientation,
                asset.TakenAt, asset.CameraMake, asset.CameraModel, asset.Lens,
                asset.Iso, asset.Aperture, asset.Shutter, asset.FocalLength, asset.UpdatedAt
            }, transaction);
        }

        if (file.IsPrimary)
        {
            await connection.ExecuteAsync(
                "UPDATE asset_files SET is_primary = 0 WHERE asset_id = @AssetId;",
                new { AssetId = assetId.Value }, transaction);
        }

        await connection.ExecuteAsync("""
            INSERT INTO asset_files
              (asset_id, folder_id, path, file_name, extension, size_bytes, modified_at,
               is_primary, file_state, error_message, created_at, updated_at)
            VALUES
              (@AssetId, @FolderId, @Path, @FileName, @Extension, @SizeBytes, @ModifiedAt,
               @IsPrimary, @FileState, @ErrorMessage, @CreatedAt, @UpdatedAt)
            ON CONFLICT(path) DO UPDATE SET
              asset_id = excluded.asset_id,
              folder_id = excluded.folder_id,
              file_name = excluded.file_name,
              extension = excluded.extension,
              size_bytes = excluded.size_bytes,
              modified_at = excluded.modified_at,
              is_primary = excluded.is_primary,
              file_state = excluded.file_state,
              error_message = excluded.error_message,
              updated_at = excluded.updated_at;
            """, new
        {
            AssetId = assetId.Value,
            file.FolderId, file.Path, file.FileName, file.Extension, file.SizeBytes,
            file.ModifiedAt, file.IsPrimary, FileState = file.State.ToString().ToLowerInvariant(),
            file.ErrorMessage, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }, transaction);

        return assetId.Value;
    }

    public async Task<IReadOnlyList<AssetFile>> GetFilesAsync(long assetId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AssetFile>("""
            SELECT id, asset_id, folder_id, path, file_name, extension, size_bytes, modified_at,
                   is_primary, file_state AS state, error_message
            FROM asset_files WHERE asset_id = @AssetId ORDER BY is_primary DESC, path;
            """, new { AssetId = assetId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<AssetFile>> GetFilesByFolderAsync(long folderId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AssetFile>(
            """
            SELECT id, asset_id, folder_id, path, file_name, extension, size_bytes, modified_at,
                   is_primary, file_state AS state, error_message
            FROM asset_files WHERE folder_id = @FolderId;
            """, new { FolderId = folderId });
        return rows.ToList();
    }

    /// <summary>按资产批量取文件行。复制与删除需要一次拿到全部路径，避免逐张查询。</summary>
    public async Task<IReadOnlyList<AssetFile>> GetFilesForAssetsAsync(
        IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return Array.Empty<AssetFile>();

        var result = new List<AssetFile>(assetIds.Count);
        await using var connection = await _db.OpenAsync(ct);
        for (var offset = 0; offset < assetIds.Count; offset += IdChunkSize)
        {
            var chunk = assetIds.Skip(offset).Take(IdChunkSize).ToList();
            var rows = await connection.QueryAsync<AssetFile>("""
                SELECT id, asset_id, folder_id, path, file_name, extension, size_bytes, modified_at,
                       is_primary, file_state AS state, error_message
                FROM asset_files WHERE asset_id IN @AssetIds;
                """, new { AssetIds = chunk });
            result.AddRange(rows);
        }

        return result;
    }

    /// <summary>
    /// 返回给定路径中已经有索引记录的部分。路径列带唯一约束，
    /// 搬迁前必须避开这些路径，否则整批写入会失败。
    /// </summary>
    public async Task<IReadOnlySet<string>> GetIndexedPathsAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return result;

        await using var connection = await _db.OpenAsync(ct);
        for (var offset = 0; offset < paths.Count; offset += IdChunkSize)
        {
            var chunk = paths.Skip(offset).Take(IdChunkSize).ToList();
            var rows = await connection.QueryAsync<string>(
                "SELECT path FROM asset_files WHERE path IN @Paths;", new { Paths = chunk });
            foreach (var path in rows) result.Add(path);
        }

        return result;
    }

    /// <summary>把文件行迁移到新位置。资产内容不变，语义向量与标签继续沿用。</summary>
    public async Task RelocateFilesAsync(
        IReadOnlyList<FileRelocation> relocations, CancellationToken ct = default)
    {
        if (relocations.Count == 0) return;

        var now = DateTime.UtcNow;
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var relocation in relocations)
        {
            await connection.ExecuteAsync("""
                UPDATE asset_files SET
                  folder_id = @FolderId,
                  path = @Path,
                  file_name = @FileName,
                  extension = @Extension,
                  size_bytes = @SizeBytes,
                  modified_at = @ModifiedAt,
                  file_state = 'indexed',
                  error_message = NULL,
                  updated_at = @Now
                WHERE id = @FileId;
                """, new
            {
                relocation.FileId, relocation.FolderId, relocation.Path, relocation.FileName,
                relocation.Extension, relocation.SizeBytes, relocation.ModifiedAt, Now = now
            }, transaction);

            // 只由主文件决定资产显示名。副本的位置变化不影响列表标题。
            await connection.ExecuteAsync("""
                UPDATE assets SET file_name = @FileName, updated_at = @Now
                WHERE id = @AssetId
                  AND EXISTS (SELECT 1 FROM asset_files WHERE id = @FileId AND is_primary = 1);
                """, new { relocation.AssetId, relocation.FileName, relocation.FileId, Now = now },
                transaction);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 登记复制出来的文件行。副本不是主文件，资产的显示名与主路径仍由原文件决定。
    /// </summary>
    public async Task AddFileCopiesAsync(
        IReadOnlyList<FileAddition> copies, CancellationToken ct = default)
    {
        if (copies.Count == 0) return;

        var now = DateTime.UtcNow;
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var copy in copies)
        {
            await connection.ExecuteAsync("""
                INSERT INTO asset_files
                  (asset_id, folder_id, path, file_name, extension, size_bytes, modified_at,
                   is_primary, file_state, error_message, created_at, updated_at)
                VALUES
                  (@AssetId, @FolderId, @Path, @FileName, @Extension, @SizeBytes, @ModifiedAt,
                   0, 'indexed', NULL, @Now, @Now);
                """, new
            {
                copy.AssetId, copy.FolderId, copy.Path, copy.FileName, copy.Extension,
                copy.SizeBytes, copy.ModifiedAt, Now = now
            }, transaction);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>删除文件行。失去全部路径的资产由 <see cref="PruneOrphansAsync"/> 清理。</summary>
    public async Task DeleteFilesAsync(
        IReadOnlyList<long> fileIds, CancellationToken ct = default)
    {
        if (fileIds.Count == 0) return;

        await using var connection = await _db.OpenAsync(ct);
        for (var offset = 0; offset < fileIds.Count; offset += IdChunkSize)
        {
            var chunk = fileIds.Skip(offset).Take(IdChunkSize).ToList();
            await connection.ExecuteAsync(
                "DELETE FROM asset_files WHERE id IN @Ids;", new { Ids = chunk });
        }
    }

    /// <summary>缺少宽高的资产及其主文件路径，供启动时补录。</summary>
    public async Task<IReadOnlyList<AssetPath>> GetMissingDimensionsAsync(
        int limit, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        var rows = await connection.QueryAsync<AssetPath>("""
            SELECT a.id AS asset_id, af.path
            FROM assets a
            JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            WHERE (a.width IS NULL OR a.width <= 0 OR a.height IS NULL OR a.height <= 0)
              AND af.file_state = 'indexed'
            ORDER BY a.id
            LIMIT @Limit;
            """, new { Limit = limit });
        return rows.ToList();
    }

    /// <summary>回写图片的显示宽高与方向。</summary>
    public async Task UpdateDimensionsAsync(
        long assetId, int width, int height, int? orientation, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync("""
            UPDATE assets
            SET width = @Width,
                height = @Height,
                orientation = COALESCE(@Orientation, orientation),
                updated_at = @Now
            WHERE id = @AssetId;
            """, new
        {
            AssetId = assetId,
            Width = width,
            Height = height,
            Orientation = orientation,
            Now = DateTime.UtcNow
        });
    }

    public async Task MarkMissingAsync(long fileId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE asset_files SET file_state = 'missing', updated_at = @Now WHERE id = @Id;",
            new { Id = fileId, Now = DateTime.UtcNow });
    }

    public async Task MarkPresentAsync(long fileId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE asset_files SET file_state = 'indexed', updated_at = @Now WHERE id = @Id;",
            new { Id = fileId, Now = DateTime.UtcNow });
    }

    /// <summary>
    /// 删除没有任何文件指向的资产。文件被原地替换后内容哈希变了，
    /// 旧资产就失去了全部路径；它的语义向量、识别结果与标签必须一起清掉，
    /// 否则索引里会留下一条永远点不开的旧记录。
    /// </summary>
    public async Task<int> PruneOrphansAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        return await connection.ExecuteAsync("""
            DELETE FROM assets
            WHERE NOT EXISTS (
                SELECT 1 FROM asset_files af WHERE af.asset_id = assets.id);
            """);
    }
}
