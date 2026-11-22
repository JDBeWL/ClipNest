using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using Dapper;

namespace ClipNest.Data.Tests;

public sealed class FolderRepositoryTests
{
    [Fact]
    public async Task Remove_DeletesFolderAndOrphanAssets_KeepsDiskFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-folders-{Guid.NewGuid():N}");
        var folderA = Path.Combine(root, "alpha");
        var folderB = Path.Combine(root, "beta");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);

        try
        {
            var fileA = Path.Combine(folderA, "one.jpg");
            var fileB = Path.Combine(folderB, "two.jpg");
            await File.WriteAllTextAsync(fileA, "a");
            await File.WriteAllTextAsync(fileB, "b");

            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var folderAId = await folders.EnsureAsync(folderA);
            var folderBId = await folders.EnsureAsync(folderB);

            var assetA = await assets.UpsertAsync(
                new Asset
                {
                    Sha256 = "hash-a", FileName = "one.jpg",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile
                {
                    FolderId = folderAId, Path = fileA, FileName = "one.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
                });
            await assets.UpsertAsync(
                new Asset
                {
                    Sha256 = "hash-b", FileName = "two.jpg",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile
                {
                    FolderId = folderBId, Path = fileB, FileName = "two.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
                });

            var all = await folders.GetAllAsync();
            Assert.Equal(2, all.Count);
            Assert.Equal("alpha", all[0].Name);
            Assert.Equal(1, all[0].AssetCount);
            Assert.Equal(1, all[1].AssetCount);

            await folders.MarkScannedAsync(folderAId);
            Assert.NotNull((await folders.GetAllAsync()).First(f => f.Id == folderAId).LastScanAt);

            await folders.RemoveAsync(folderAId);

            var remaining = await folders.GetAllAsync();
            Assert.Single(remaining);
            Assert.Equal(folderBId, remaining[0].Id);
            Assert.Empty(await assets.GetFilesByFolderAsync(folderAId));

            await using var connection = await db.OpenAsync();
            Assert.Equal(
                0,
                await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM assets WHERE id = @Id;", new { Id = assetA }));
            Assert.Equal(
                1,
                await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM assets;"));

            Assert.True(File.Exists(fileA));
            Assert.True(File.Exists(fileB));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
