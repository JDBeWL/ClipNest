using ClipNest.App.ViewModels;
using ClipNest.Core.Models;
using ClipNest.Core.Tagging;
using ClipNest.Data;
using ClipNest.Data.Repositories;

namespace ClipNest.App.Tests;

public sealed class TagEditingTests
{
    [Fact]
    public async Task AddTag_AssignsTagToSelectedAsset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folders = new FolderRepository(db);
            var folderId = await folders.EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);
            var assetId = await assets.UpsertAsync(
                new Asset { Sha256 = "a", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\a.jpg", FileName = "a.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            var vm = new MainWindowViewModel(null, tags);
            vm.SelectedItem = new AssetListItem { Id = assetId, FileName = "a.jpg" };
            vm.NewTag = "旅行";

            await vm.AddTagAsync();

            var assigned = await tags.GetTagsForAssetAsync(assetId);
            Assert.Contains(assigned, tag => tag.Name == "旅行");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CommitCharactersAndSeries_ReplacesCategoryTags()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        try
        {
            var folders = new FolderRepository(db);
            var folderId = await folders.EnsureAsync(@"C:\photos");
            var assets = new AssetRepository(db);
            var tags = new TagRepository(db);
            var assetId = await assets.UpsertAsync(
                new Asset { Sha256 = "a", FileName = "a.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folderId, Path = @"C:\photos\a.jpg", FileName = "a.jpg", Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });

            var vm = new MainWindowViewModel(null, tags);
            vm.SelectedItem = new AssetListItem { Id = assetId, FileName = "a.jpg" };

            vm.CharacterDraft = "月村手毬，藤田琴音";
            await vm.CommitCharactersCommand.ExecuteAsync(null);

            var characters = await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Character);
            Assert.Equal(
                new[] { "月村手毬", "藤田琴音" }.OrderBy(name => name).ToArray(),
                characters.Select(tag => tag.Name).OrderBy(name => name).ToArray());

            vm.CharacterDraft = "";
            await vm.CommitCharactersCommand.ExecuteAsync(null);
            Assert.Empty(await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Character));

            vm.SeriesDraft = "学园偶像大师";
            await vm.CommitSeriesCommand.ExecuteAsync(null);
            var series = await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Series);
            Assert.Single(series);
            Assert.Equal("学园偶像大师", series[0].Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
