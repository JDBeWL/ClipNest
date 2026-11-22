using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;

namespace ClipNest.Data.Tests;

public sealed class GalaxyRepositoryTests
{
    [Fact]
    public async Task Snapshot_ReturnsAssetsWithTagsAndPrimaryPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-galaxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "a.jpg");
            await File.WriteAllTextAsync(file, "a");

            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);
            var folderId = await folders.EnsureAsync(root);
            var assetId = await assets.UpsertAsync(
                new Asset { Sha256 = "hash-a", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = file, FileName = "a.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });
            var tagId = await tags.CreateAsync("旅行", null);
            await tags.AssignAsync(assetId, tagId);

            var snapshot = await new GalaxyRepository(db).GetSnapshotAsync();

            var asset = Assert.Single(snapshot.Assets);
            Assert.Equal("a.jpg", asset.Asset.FileName);
            Assert.Equal(file, asset.Asset.PrimaryPath);
            Assert.Equal(new[] { tagId }, asset.TagIds);
            var tag = Assert.Single(snapshot.Tags);
            Assert.Equal("旅行", tag.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Snapshot_RespectsMaxAssetsLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-galaxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var folderId = await folders.EnsureAsync(root);

            await AddAsync(assets, folderId, root, "old.jpg", "hash-old", new DateTime(2020, 1, 1));
            await AddAsync(assets, folderId, root, "new.jpg", "hash-new", new DateTime(2021, 1, 1));

            var snapshot = await new GalaxyRepository(db).GetSnapshotAsync(maxAssets: 1);

            var asset = Assert.Single(snapshot.Assets);
            Assert.Equal("new.jpg", asset.Asset.FileName);
            Assert.Empty(asset.TagIds);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Snapshot_EmptyDatabase_ReturnsNoAssetsOrTags()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-galaxy-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var snapshot = await new GalaxyRepository(db).GetSnapshotAsync();

            Assert.Empty(snapshot.Assets);
            Assert.Empty(snapshot.Tags);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Snapshot_IncludesEmbeddings_WhenModelIdProvided()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-galaxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "a.jpg");
            await File.WriteAllTextAsync(file, "a");

            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var folderId = await new FolderRepository(db).EnsureAsync(root);
            var assetId = await new AssetRepository(db).UpsertAsync(
                new Asset { Sha256 = "hash-embed", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = file, FileName = "a.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            const string modelId = "chinese-clip-vit-base-patch16";
            await new EmbeddingRepository(db).UpsertAsync(assetId, modelId, new[] { 1f, 0f });

            var galaxy = new GalaxyRepository(db);

            var snapshot = await galaxy.GetSnapshotAsync(embeddingModelId: modelId);
            Assert.True(snapshot.Embeddings.TryGetValue(assetId, out var vector));
            Assert.NotNull(vector);
            Assert.Equal(2, vector!.Length);
            Assert.Equal(1f, vector[0], 2);
            Assert.Equal(0f, vector[1], 2);

            var withoutModel = await galaxy.GetSnapshotAsync();
            Assert.Empty(withoutModel.Embeddings);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task AddAsync(
        AssetRepository assets, long folderId, string root,
        string fileName, string sha256, DateTime takenAt)
    {
        var file = Path.Combine(root, fileName);
        await File.WriteAllTextAsync(file, fileName);
        await assets.UpsertAsync(
            new Asset
            {
                Sha256 = sha256, FileName = fileName, TakenAt = takenAt,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            },
            new AssetFile
            {
                FolderId = folderId, Path = file, FileName = fileName, Extension = ".jpg",
                SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
            });
    }
}
