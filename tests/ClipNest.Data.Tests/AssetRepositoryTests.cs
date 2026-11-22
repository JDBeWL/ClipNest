using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;

namespace ClipNest.Data.Tests;

public sealed class AssetRepositoryTests
{
    private static async Task<(ClipNestDatabase Db, string Path)> CreateDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        return (db, path);
    }

    [Fact]
    public async Task Upsert_SameHash_TwoPaths_CreatesOneAsset()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var folders = new FolderRepository(db);
            var folderId = await folders.EnsureAsync(@"C:\photos");

            var repo = new AssetRepository(db);
            var asset = new Asset
            {
                Sha256 = "hash-1",
                FileName = "a.jpg",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var file1 = new AssetFile
            {
                FolderId = folderId,
                Path = @"C:\photos\a.jpg",
                FileName = "a.jpg",
                Extension = ".jpg",
                SizeBytes = 10,
                ModifiedAt = DateTime.UtcNow,
                IsPrimary = true
            };
            var file2 = new AssetFile
            {
                FolderId = folderId,
                Path = @"C:\backup\a.jpg",
                FileName = "a.jpg",
                Extension = ".jpg",
                SizeBytes = 10,
                ModifiedAt = DateTime.UtcNow,
                IsPrimary = false
            };

            var id1 = await repo.UpsertAsync(asset, file1);
            var id2 = await repo.UpsertAsync(asset, file2);

            Assert.Equal(id1, id2);
            Assert.Equal(2, (await repo.GetFilesAsync(id1)).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Upsert_NewPrimary_ClearsOldPrimary()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var repo = new AssetRepository(db);
            var asset = new Asset { Sha256 = "hash-2", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

            await repo.UpsertAsync(asset, new AssetFile { FolderId = folderId, Path = @"C:\photos\a.jpg", FileName = "a.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });
            await repo.UpsertAsync(asset, new AssetFile { FolderId = folderId, Path = @"C:\photos\b.jpg", FileName = "b.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            var files = await repo.GetFilesAsync(1);
            Assert.Single(files, f => f.IsPrimary);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingDimensions_AreListedAndBackfilled()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var repo = new AssetRepository(db);
            var id = await repo.UpsertAsync(
                new Asset
                {
                    Sha256 = "hash-dim", FileName = "a.png",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile
                {
                    FolderId = folderId, Path = @"C:\photos\a.png", FileName = "a.png",
                    Extension = ".png", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
                });

            var missing = await repo.GetMissingDimensionsAsync(10);
            Assert.Single(missing);
            Assert.Equal(id, missing[0].AssetId);

            await repo.UpdateDimensionsAsync(id, 608, 1079, 1);
            Assert.Empty(await repo.GetMissingDimensionsAsync(10));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
