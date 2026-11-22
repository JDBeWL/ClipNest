using Dapper;

namespace ClipNest.Data.Repositories;

public sealed class ThumbnailRepository
{
    private readonly ClipNestDatabase _db;

    public ThumbnailRepository(ClipNestDatabase db) => _db = db;

    public async Task UpsertAsync(
        long assetId,
        int size,
        string cachePath,
        int width,
        int height,
        string source,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO thumbnails (asset_id, size, cache_path, width, height, source, created_at)
            VALUES (@AssetId, @Size, @CachePath, @Width, @Height, @Source, @CreatedAt)
            ON CONFLICT(asset_id, size) DO UPDATE SET
              cache_path = excluded.cache_path,
              width = excluded.width,
              height = excluded.height,
              source = excluded.source,
              created_at = excluded.created_at;
            """, new
        {
            AssetId = assetId,
            Size = size,
            CachePath = cachePath,
            Width = width,
            Height = height,
            Source = source,
            CreatedAt = DateTime.UtcNow
        });
    }
}
