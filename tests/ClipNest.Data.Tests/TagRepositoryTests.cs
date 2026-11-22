using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;

namespace ClipNest.Data.Tests;

public sealed class TagRepositoryTests
{
    [Fact]
    public async Task Resolve_TagPath_IncludesDescendants()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var repo = new TagRepository(db);
            var travel = await repo.CreateAsync("旅行", null);
            var japan = await repo.CreateAsync("日本", travel);
            var tokyo = await repo.CreateAsync("东京", japan);

            var all = await repo.GetTagIdsByPathAsync("旅行", includeDescendants: true);
            Assert.Equal(3, all.Count);

            var exact = await repo.GetTagIdsByPathAsync("旅行/日本", includeDescendants: false);
            Assert.Single(exact);
            Assert.Equal(japan, exact[0]);

            var tree = await repo.GetTreeAsync();
            Assert.Single(tree);
            Assert.Single(tree[0].Children);
            Assert.Single(tree[0].Children[0].Children);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Create_DuplicateRootTag_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var repo = new TagRepository(db);
            await repo.CreateAsync("旅行", null);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => repo.CreateAsync("旅行", null));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task PruneUnused_RemovesTagsLeftBehindByRemovedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-tags-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "gallery");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "one.jpg");
        await File.WriteAllTextAsync(file, "x");

        var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
        await db.InitializeAsync();
        try
        {
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);

            var folderId = await folders.EnsureAsync(folder);
            var assetId = await assets.UpsertAsync(
                new Asset
                {
                    Sha256 = "hash-one", FileName = "one.jpg",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile
                {
                    FolderId = folderId, Path = file, FileName = "one.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
                });

            var character = await tags.CreateAsync("角色", null);
            var name = await tags.CreateAsync("Kazusa", character);
            await tags.AssignAsync(assetId, name);

            // 文件夹移除会删掉图片，asset_tags 随外键级联清空，标签本身留在 tags 里。
            await folders.RemoveAsync(folderId);
            var tree = await tags.GetTreeAsync();
            Assert.Single(tree);
            Assert.Single(tree[0].Children);

            var removed = await tags.PruneUnusedAsync();
            Assert.Equal(2, removed);
            Assert.Empty(await tags.GetTreeAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PruneUnused_KeepsTagStillUsedByAnotherAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-tags-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "gallery");
        Directory.CreateDirectory(folder);
        var fileA = Path.Combine(folder, "a.jpg");
        var fileB = Path.Combine(folder, "b.jpg");
        await File.WriteAllTextAsync(fileA, "a");
        await File.WriteAllTextAsync(fileB, "b");

        var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
        await db.InitializeAsync();
        try
        {
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);

            var folderId = await folders.EnsureAsync(folder);
            var assetA = await assets.UpsertAsync(
                new Asset
                {
                    Sha256 = "hash-a", FileName = "a.jpg",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile
                {
                    FolderId = folderId, Path = fileA, FileName = "a.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
                });
            var assetB = await assets.UpsertAsync(
                new Asset
                {
                    Sha256 = "hash-b", FileName = "b.jpg",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile
                {
                    FolderId = folderId, Path = fileB, FileName = "b.jpg", Extension = ".jpg",
                    SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
                });

            var tagId = await tags.CreateAsync("1girl", null);
            await tags.AssignAsync(assetA, tagId);
            await tags.AssignAsync(assetB, tagId);

            await tags.RemoveAsync(assetA, tagId);
            Assert.Equal(0, await tags.PruneUnusedAsync());

            await tags.RemoveAsync(assetB, tagId);
            Assert.Equal(1, await tags.PruneUnusedAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
