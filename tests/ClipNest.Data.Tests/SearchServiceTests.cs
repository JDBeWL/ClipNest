using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using ClipNest.Data.Search;

namespace ClipNest.Data.Tests;

public sealed class SearchServiceTests
{
    [Fact]
    public async Task Search_Tag_And_Not_ReturnsExpectedAsset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);

            var a = await assets.UpsertAsync(
                new Asset { Sha256 = "a", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\a.jpg", FileName = "a.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });
            var b = await assets.UpsertAsync(
                new Asset { Sha256 = "b", FileName = "b.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\b.jpg", FileName = "b.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            var travel = await tags.CreateAsync("旅行", null);
            var night = await tags.CreateAsync("夜景", null);
            var person = await tags.CreateAsync("人像", null);
            await tags.AssignAsync(a, travel);
            await tags.AssignAsync(a, night);
            await tags.AssignAsync(b, travel);
            await tags.AssignAsync(b, night);
            await tags.AssignAsync(b, person);

            var service = new AssetSearchService(tags, new SearchRepository(db));
            var result = await service.SearchAsync("#旅行 #夜景 -#人像");

            Assert.Single(result);
            Assert.Equal(a, result[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Unlimited_Limit_Returns_All_Assets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var now = DateTime.UtcNow;
            for (var i = 0; i < 620; i++)
            {
                await assets.UpsertAsync(
                    new Asset { Sha256 = $"sha{i}", FileName = $"{i}.jpg", CreatedAt = now, UpdatedAt = now },
                    new AssetFile
                    {
                        FolderId = folderId,
                        Path = $@"C:\photos\{i}.jpg",
                        FileName = $"{i}.jpg",
                        Extension = ".jpg",
                        SizeBytes = 1,
                        ModifiedAt = now,
                        IsPrimary = true
                    });
            }

            var service = new AssetSearchService(new TagRepository(db), new SearchRepository(db));

            Assert.Equal(620, (await service.GetRecentAsync(int.MaxValue)).Count);
            Assert.Equal(620, (await service.SearchAsync(@"folder:""C:\photos""", int.MaxValue)).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_HidesMissingFiles_UnlessExplicitlyRequested()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var service = new AssetSearchService(new TagRepository(db), new SearchRepository(db));
            var now = DateTime.UtcNow;

            var id = await assets.UpsertAsync(
                new Asset { Sha256 = "gone", FileName = "gone.jpg", CreatedAt = now, UpdatedAt = now },
                new AssetFile
                {
                    FolderId = folderId,
                    Path = @"C:\photos\gone.jpg",
                    FileName = "gone.jpg",
                    Extension = ".jpg",
                    SizeBytes = 1,
                    ModifiedAt = now,
                    IsPrimary = true
                });

            Assert.Single(await service.GetRecentAsync());
            Assert.Single(await service.SearchAsync("folder:\"C:\\photos\""));

            var file = Assert.Single(await assets.GetFilesAsync(id));
            await assets.MarkMissingAsync(file.Id);

            // 刷新后列表与文件夹视图都不再显示已删除的图。
            Assert.Empty(await service.GetRecentAsync());
            Assert.Empty(await service.SearchAsync("folder:\"C:\\photos\""));

            // 显式查缺失文件时仍然能看到它。
            var missing = Assert.Single(await service.SearchAsync("is:missing"));
            Assert.Equal(id, missing.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_PlainText_AlsoMatchesTagNames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);

            var tagged = await assets.UpsertAsync(
                new Asset { Sha256 = "tagged", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\a.jpg", FileName = "a.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });
            var other = await assets.UpsertAsync(
                new Asset { Sha256 = "other", FileName = "b.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\b.jpg", FileName = "b.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            var tagId = await tags.CreateAsync("初音未来", null);
            await tags.AssignAsync(tagged, tagId);

            var service = new AssetSearchService(tags, new SearchRepository(db));
            var result = Assert.Single(await service.SearchAsync("初音"));

            Assert.Equal(tagged, result.Id);
            Assert.NotEqual(other, result.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_Field_Filters_Work()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var id = await assets.UpsertAsync(
                new Asset
                {
                    Sha256 = "c", FileName = "c.heic", TakenAt = new DateTime(2024, 5, 1),
                    Rating = 5, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\c.heic", FileName = "c.heic", Extension = ".heic", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            var service = new AssetSearchService(new TagRepository(db), new SearchRepository(db));
            var result = await service.SearchAsync("year:2024 ext:heic rating:>=4");

            Assert.Single(result);
            Assert.Equal(id, result[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
