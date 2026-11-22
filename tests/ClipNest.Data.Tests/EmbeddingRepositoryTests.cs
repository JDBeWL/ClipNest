using ClipNest.Core.Models;
using ClipNest.Data.Repositories;
using Dapper;

namespace ClipNest.Data.Tests;

public sealed class EmbeddingRepositoryTests
{
    private const string ModelId = "chinese-clip-vit-base-patch16";

    private static async Task<(ClipNestDatabase Db, string Path)> CreateDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-embedding-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        return (db, path);
    }

    private static async Task<long> AddAssetAsync(
        ClipNestDatabase db, string hash, string fileName, string folderPath = @"C:\photos")
    {
        var folderId = await new FolderRepository(db).EnsureAsync(folderPath);
        var repo = new AssetRepository(db);
        var now = DateTime.UtcNow;

        return await repo.UpsertAsync(
            new Asset
            {
                Sha256 = hash,
                FileName = fileName,
                CreatedAt = now,
                UpdatedAt = now
            },
            new AssetFile
            {
                FolderId = folderId,
                Path = Path.Combine(folderPath, fileName),
                FileName = fileName,
                Extension = ".jpg",
                SizeBytes = 10,
                ModifiedAt = now,
                IsPrimary = true
            });
    }

    [Fact]
    public async Task Stats_EmptyDatabase_ReturnsZeros()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            // 空库时 GROUP BY 返回 0 行，Dapper 无法在此物化 record。
            var stats = await new EmbeddingRepository(db).GetStatsAsync();
            Assert.Equal(0, stats.Pending);
            Assert.Equal(0, stats.Ready);
            Assert.Equal(0, stats.Error);
            Assert.Equal(0, stats.Skipped);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Pending_Upsert_ReadBack_AndStats()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var asset1 = await AddAssetAsync(db, "hash-1", "a.jpg");
            var asset2 = await AddAssetAsync(db, "hash-2", "b.jpg");
            var repo = new EmbeddingRepository(db);

            var pending = await repo.GetPendingAsync();
            Assert.Equal(2, pending.Count);
            Assert.Equal(new[] { asset1, asset2 }, pending.Select(j => j.AssetId).ToArray());
            Assert.Equal("hash-1", pending[0].Sha256);
            Assert.Equal("a.jpg", pending[0].FileName);
            Assert.Equal(".jpg", pending[0].Extension);

            var vector = new[] { 1f, 0.5f, -0.25f };
            await repo.UpsertAsync(asset1, ModelId, vector);

            var stats = await repo.GetStatsAsync();
            Assert.Equal(1, stats.Ready);
            Assert.Equal(1, stats.Pending);
            Assert.Equal(0, stats.Error);
            Assert.Equal(0, stats.Skipped);

            Assert.Single(await repo.GetPendingAsync());

            var vectors = await repo.GetAllVectorsAsync(ModelId);
            Assert.Single(vectors);
            Assert.True(vectors.TryGetValue(asset1, out var stored));
            Assert.Equal(vector.Length, stored!.Length);
            for (var i = 0; i < vector.Length; i++)
            {
                Assert.InRange(stored[i], vector[i] - 0.01f, vector[i] + 0.01f);
            }

            // 重复 Upsert 覆盖同一行，不会新增。
            await repo.UpsertAsync(asset1, ModelId, new[] { 0f, 1f, 0f });
            var updated = await repo.GetAllVectorsAsync(ModelId);
            Assert.Single(updated);
            Assert.InRange(updated[asset1][0], -0.01f, 0.01f);
            Assert.InRange(updated[asset1][1], 0.99f, 1.01f);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Stats_IgnoresAssetsWhoseFileIsMissing()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var kept = await AddAssetAsync(db, "hash-1", "a.jpg");
            var gone = await AddAssetAsync(db, "hash-2", "b.jpg");
            var repo = new EmbeddingRepository(db);
            await repo.UpsertAsync(kept, ModelId, new[] { 1f, 0f });

            // 磁盘上已删除的图片不会再有向量，算进分母会让进度到不了总数。
            await using (var connection = await db.OpenAsync())
            {
                var fileId = await connection.ExecuteScalarAsync<long>(
                    "SELECT id FROM asset_files WHERE asset_id = @AssetId AND is_primary = 1;",
                    new { AssetId = gone });
                await new AssetRepository(db).MarkMissingAsync(fileId);
            }

            var stats = await repo.GetStatsAsync();
            Assert.Equal(1, stats.Ready);
            Assert.Equal(0, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task PruneOrphans_RemovesReplacedAssetAndItsVector()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var embeddings = new EmbeddingRepository(db);
            var assets = new AssetRepository(db);

            var original = await AddAssetAsync(db, "hash-old", "a.jpg");
            await embeddings.UpsertAsync(original, ModelId, new[] { 1f, 0f });

            // 同一路径换了内容。新哈希生成新资产，旧资产失去全部路径。
            var replacement = await AddAssetAsync(db, "hash-new", "a.jpg");
            Assert.NotEqual(original, replacement);
            Assert.Single(await embeddings.GetAllVectorsAsync(ModelId));

            Assert.Equal(1, await assets.PruneOrphansAsync());

            // 旧资产的向量随外键级联删除，索引里不再有已经换掉的图片。
            Assert.Empty(await embeddings.GetAllVectorsAsync(ModelId));
            var stats = await embeddings.GetStatsAsync();
            Assert.Equal(1, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MarkStatusAsync_UpdatesStatusForAssetAndFrom()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var asset1 = await AddAssetAsync(db, "hash-1", "a.jpg");
            await AddAssetAsync(db, "hash-2", "b.jpg");
            var repo = new EmbeddingRepository(db);

            await repo.MarkStatusAsync(asset1, "error");
            var stats = await repo.GetStatsAsync();
            Assert.Equal(1, stats.Error);
            Assert.Equal(1, stats.Pending);

            var affected = await repo.MarkStatusAsync("error", "skipped");
            Assert.Equal(1, affected);
            stats = await repo.GetStatsAsync();
            Assert.Equal(1, stats.Skipped);
            Assert.Equal(0, stats.Error);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
