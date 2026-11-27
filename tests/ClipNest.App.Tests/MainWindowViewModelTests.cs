using ClipNest.Ai.Tagging;
using ClipNest.App.Services;
using ClipNest.App.ViewModels;
using ClipNest.Core.Models;
using ClipNest.Core.Tagging;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using ClipNest.Data.Search;

namespace ClipNest.App.Tests;

public sealed class MainWindowViewModelTests
{
    private sealed class FakeSearch : IAssetSearchService
    {
        public string? LastQuery { get; private set; }

        public IReadOnlyList<AssetListItem> Items { get; set; } = new[]
        {
            new AssetListItem { Id = 1, FileName = "a.jpg" }
        };

        public Task<IReadOnlyList<AssetListItem>> SearchAsync(
            string queryText, int limit = 200, CancellationToken ct = default)
        {
            LastQuery = queryText;
            return Task.FromResult(Items);
        }

        public Task<IReadOnlyList<AssetListItem>> GetRecentAsync(
            int limit = 500, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AssetListItem>>(Array.Empty<AssetListItem>());
    }

    [Fact]
    public void Default_View_Is_Grid()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(ViewMode.Grid, vm.CurrentView);
        Assert.True(vm.IsGrid);

        vm.ShowWallCommand.Execute(null);
        Assert.True(vm.IsLightWall);
    }

    [Fact]
    public void Settings_Commands_Toggle_Page()
    {
        var vm = new MainWindowViewModel();

        vm.OpenSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsOpen);

        vm.CloseSettingsCommand.Execute(null);
        Assert.False(vm.IsSettingsOpen);
    }

    [Fact]
    public void AutoTagging_Toggle_Persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            var vm = new MainWindowViewModel(settings: settings);

            vm.AutoTaggingEnabled = false;

            var reloaded = new SettingsService(path);
            Assert.False(reloaded.Settings.AutoTaggingEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Appearance_Mode_Normalizes_Unknown_Value()
    {
        Assert.Equal(AppearanceModes.System, AppearanceModes.Normalize("unknown"));
        Assert.Equal(AppearanceModes.System, AppearanceModes.Normalize(AppearanceModes.System));
        Assert.Equal(AppearanceModes.Light, AppearanceModes.Normalize(AppearanceModes.Light));
        Assert.Equal(AppearanceModes.Dark, AppearanceModes.Normalize(AppearanceModes.Dark));
    }

    [Fact]
    public async Task Search_Updates_Items()
    {
        var vm = new MainWindowViewModel(new FakeSearch());
        vm.SearchText = "#旅行";

        await vm.SearchAsync();

        Assert.Single(vm.Items);
        Assert.Equal("a.jpg", vm.Items[0].FileName);
    }

    [Fact]
    public async Task FilterByTag_SearchesTagPath()
    {
        var vm = new MainWindowViewModel(new FakeSearch());

        await vm.FilterByTagAsync("旅行/夜景");

        Assert.Equal("#旅行/夜景", vm.SearchText);
        Assert.Contains(1L, vm.HighlightedIds!);
    }

    [Fact]
    public async Task FilterByTag_QuotesPathWithSpaces()
    {
        var vm = new MainWindowViewModel(new FakeSearch());

        await vm.FilterByTagAsync("fujita kotone");

        Assert.Equal("#\"fujita kotone\"", vm.SearchText);
    }

    [Fact]
    public void Switching_Folder_Updates_Collection_Title()
    {
        var vm = new MainWindowViewModel(new FakeSearch());
        var longStrip = new Folder { Id = 1, Path = @"E:\pic\长图测试" };
        var kazusa = new Folder { Id = 2, Path = @"E:\pic\Kazusa" };
        vm.Folders.Add(longStrip);
        vm.Folders.Add(kazusa);

        vm.SelectedFolder = longStrip;
        Assert.Equal("长图测试", vm.CollectionTitle);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.SelectedFolder = kazusa;

        // 两个文件夹之间切换时 ActiveCollection 一直是 Folder，标题必须单独通知。
        Assert.Contains(nameof(MainWindowViewModel.CollectionTitle), changed);
        Assert.Equal("Kazusa", vm.CollectionTitle);
        Assert.Equal("文件夹", vm.CollectionKind);
    }

    [Fact]
    public async Task Search_WithSemanticEnabled_RewritesPlainText()
    {
        var fake = new FakeSearch();
        var vm = new MainWindowViewModel(fake) { SemanticEnabled = true };

        vm.SearchText = "银发魔女";
        await vm.SearchAsync();
        Assert.Equal("sim:\"银发魔女\"", fake.LastQuery);

        // 标签查询不转为语义查询。
        vm.SearchText = "#旅行";
        await vm.SearchAsync();
        Assert.Equal("#旅行", fake.LastQuery);
    }

    [Fact]
    public async Task Search_WithSemanticEnabled_PrefersExistingTag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        try
        {
            var db = new ClipNestDatabase(path);
            await db.InitializeAsync();
            var tags = new TagRepository(db);
            await tags.CreateAsync("girls' frontline", null);

            var fake = new FakeSearch();
            var vm = new MainWindowViewModel(fake, tags) { SemanticEnabled = true };

            vm.SearchText = "girls' frontline";
            await vm.SearchAsync();

            Assert.Equal("#\"girls' frontline\"", fake.LastQuery);
            Assert.False(vm.IsSemanticResult);

            vm.SearchText = "银发魔女";
            await vm.SearchAsync();

            Assert.Equal("sim:\"银发魔女\"", fake.LastQuery);
            Assert.True(vm.IsSemanticResult);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_WithSemanticEnabled_MarksResultsAsRelevanceRanked()
    {
        var vm = new MainWindowViewModel(new FakeSearch()) { SemanticEnabled = true };

        vm.SearchText = "银发魔女";
        await vm.SearchAsync();
        Assert.True(vm.IsSemanticResult);

        // 回到图库后恢复瀑布流。
        await vm.ShowAllAsync();
        Assert.False(vm.IsSemanticResult);
    }

    [Fact]
    public async Task Search_WithoutSemantic_KeepsMasonryLayout()
    {
        var vm = new MainWindowViewModel(new FakeSearch());

        vm.SearchText = "银发魔女";
        await vm.SearchAsync();

        Assert.False(vm.IsSemanticResult);
    }

    [Fact]
    public async Task SemanticResults_KeepRankOrderDespiteSortMode()
    {
        var fake = new FakeSearch
        {
            Items = new[]
            {
                new AssetListItem { Id = 3, FileName = "c.jpg" },
                new AssetListItem { Id = 1, FileName = "a.jpg" },
                new AssetListItem { Id = 2, FileName = "b.jpg" }
            }
        };
        var vm = new MainWindowViewModel(fake) { SemanticEnabled = true, SortMode = "name" };

        vm.SearchText = "银发魔女";
        await vm.SearchAsync();

        Assert.Equal(new long[] { 3, 1, 2 }, vm.Items.Select(item => item.Id));
    }

    [Fact]
    public void SemanticStatus_DefaultsToNotInstalled()
    {
        var vm = new MainWindowViewModel();

        Assert.True(
            vm.SemanticStatus.Contains("语义搜索不可用") || vm.SemanticStatus.Contains("未安装"),
            $"实际状态：{vm.SemanticStatus}");
        Assert.False(vm.IsModelReady);
    }

    [Fact]
    public void ViewHint_FollowsCurrentView()
    {
        var vm = new MainWindowViewModel();
        Assert.Contains("双击", vm.ViewHint);

        vm.ShowGalaxyCommand.Execute(null);
        Assert.Contains("拖拽", vm.ViewHint);

        vm.ShowWallCommand.Execute(null);
        Assert.Contains("悬停", vm.ViewHint);
    }

    [Fact]
    public void BatchBar_ShowsOnlyForMultiSelectionInGrid()
    {
        var vm = new MainWindowViewModel();
        var items = new[]
        {
            new AssetListItem { Id = 1, FileName = "a.jpg" },
            new AssetListItem { Id = 2, FileName = "b.jpg" }
        };

        vm.ApplySelection(new[] { items[0] });
        Assert.False(vm.ShowBatchBar);

        vm.ApplySelection(items);
        Assert.True(vm.ShowBatchBar);

        // 光墙与星系只支持单选，多选操作条不应出现在这两个视图。
        vm.ShowWallCommand.Execute(null);
        Assert.False(vm.ShowBatchBar);
    }

    [Fact]
    public async Task LoadTags_PopulatesTree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        try
        {
            var db = new ClipNestDatabase(path);
            await db.InitializeAsync();
            var tags = new TagRepository(db);
            await tags.CreateAsync("旅行", null);

            var vm = new MainWindowViewModel(null, tags);
            await vm.LoadTagsAsync();

            Assert.Single(vm.Tags);
            Assert.Equal("旅行", vm.Tags[0].Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadGalaxy_PopulatesClustersAndNodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        try
        {
            var db = new ClipNestDatabase(path);
            await db.InitializeAsync();
            var tags = new TagRepository(db);
            var assets = new AssetRepository(db);
            var folder = await new FolderRepository(db).EnsureAsync(@"C:\photos");
            var id = await assets.UpsertAsync(
                new Asset { Sha256 = "g1", FileName = "g.jpg", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AssetFile { FolderId = folder, Path = @"C:\photos\g.jpg", FileName = "g.jpg",
                    Extension = ".jpg", SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true });
            await tags.AssignAsync(id, await tags.CreateAsync("旅行", null));

            var vm = new MainWindowViewModel(galaxy: new GalaxyRepository(db));
            await vm.LoadGalaxyAsync();

            Assert.Single(vm.GalaxyClusters);
            Assert.Single(vm.GalaxyNodes);
            Assert.Equal("旅行", vm.GalaxyClusters[0].Label);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_SetsHighlightIds()
    {
        var vm = new MainWindowViewModel(new FakeSearch());
        vm.SearchText = "#旅行";

        await vm.SearchAsync();

        Assert.NotNull(vm.HighlightedIds);
        Assert.Contains(1L, vm.HighlightedIds!);
    }

    [Fact]
    public async Task SortMode_ReordersItemsInBackground()
    {
        var fake = new FakeSearch
        {
            Items = new[]
            {
                new AssetListItem { Id = 3, FileName = "c.jpg" },
                new AssetListItem { Id = 1, FileName = "a.jpg" },
                new AssetListItem { Id = 2, FileName = "b.jpg" }
            }
        };
        var vm = new MainWindowViewModel(fake);

        vm.SearchText = "#x";
        await vm.SearchAsync();
        Assert.Equal(new long[] { 3, 1, 2 }, vm.Items.Select(item => item.Id));

        vm.SortMode = "name";
        await vm.ApplySortInPlaceAsync();

        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, vm.Items.Select(item => item.FileName));
    }

    [Fact]
    public async Task Sort_KeepsSelectionWhenItemsRemain()
    {
        var fake = new FakeSearch
        {
            Items = new[]
            {
                new AssetListItem { Id = 3, FileName = "c.jpg" },
                new AssetListItem { Id = 1, FileName = "a.jpg" }
            }
        };
        var vm = new MainWindowViewModel(fake);

        vm.SearchText = "#x";
        await vm.SearchAsync();
        vm.ApplySelection(vm.Items.ToList());
        Assert.Equal(2, vm.SelectionCount);

        vm.SortMode = "name";
        await vm.ApplySortInPlaceAsync();

        Assert.Equal(2, vm.SelectionCount);
        Assert.Equal(2, vm.SelectedItems.Count);
    }

    [Fact]
    public async Task Search_DropsSelectionThatNoLongerExists()
    {
        var fake = new FakeSearch
        {
            Items = new[]
            {
                new AssetListItem { Id = 1, FileName = "a.jpg" },
                new AssetListItem { Id = 2, FileName = "b.jpg" }
            }
        };
        var vm = new MainWindowViewModel(fake);

        vm.SearchText = "#x";
        await vm.SearchAsync();
        vm.ApplySelection(vm.Items.ToList());
        Assert.Equal(2, vm.SelectionCount);

        fake.Items = new[] { new AssetListItem { Id = 9, FileName = "z.jpg" } };
        await vm.SearchAsync();

        Assert.Equal(0, vm.SelectionCount);
        Assert.Empty(vm.SelectedItems);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public async Task LoadFolders_PopulatesList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        try
        {
            var db = new ClipNestDatabase(path);
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            await folders.EnsureAsync(@"C:\photos");

            var vm = new MainWindowViewModel(folders: folders);
            await vm.LoadFoldersAsync();

            Assert.Single(vm.Folders);
            Assert.True(vm.HasFolders);
            Assert.Equal("photos", vm.Folders[0].Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveFolder_RemovesFromList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        try
        {
            var db = new ClipNestDatabase(path);
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            await folders.EnsureAsync(@"C:\photos");
            await folders.EnsureAsync(@"C:\backup");

            var vm = new MainWindowViewModel(folders: folders);
            await vm.LoadFoldersAsync();
            var photos = vm.Folders.First(folder => folder.Name == "photos");

            await vm.RemoveFolderAsync(photos);

            Assert.Single(vm.Folders);
            Assert.Equal("backup", vm.Folders[0].Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Inspector_Overview_And_Selection_Metadata_Update()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal("图库概览", vm.InspectorHeaderTitle);
        Assert.False(vm.ShowCollectionKind);
        Assert.False(vm.HasSelectedExtension);

        vm.SelectedItem = new AssetListItem { Id = 1, FileName = "elaina.webp", Width = 1200, Height = 1800 };

        Assert.Equal("详情", vm.InspectorHeaderTitle);
        Assert.Equal("WEBP", vm.SelectedExtension);
        Assert.True(vm.HasSelectedExtension);

        vm.OpenAiSettingsCommand.Execute(null);
        Assert.True(vm.IsSettingsOpen);
        Assert.True(vm.IsAiSection);
    }

    [Fact]
    public void InferenceThreads_Stepper_Clamps_And_Persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Settings.InferenceThreads = 1;
            var vm = new MainWindowViewModel(settings: settings);

            Assert.Equal(1, vm.InferenceThreadsValue);
            Assert.False(vm.CanDecreaseInferenceThreads);

            vm.DecreaseInferenceThreadsCommand.Execute(null);
            Assert.Equal(1, vm.InferenceThreadsValue);

            if (Environment.ProcessorCount > 1)
            {
                Assert.True(vm.CanIncreaseInferenceThreads);
                vm.IncreaseInferenceThreadsCommand.Execute(null);
                Assert.Equal(2, vm.InferenceThreadsValue);
                Assert.Equal("2", vm.InferenceThreadsDisplay);
                Assert.True(vm.CanDecreaseInferenceThreads);

                var reloaded = new SettingsService(path);
                Assert.Equal(2, reloaded.Settings.InferenceThreads);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class StubTagger : ITagger
    {
        public bool IsAvailable => false;
        public Task<AiTagResult> TagAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(AiTagResult.Empty);
        public Task ReleaseIfIdleAsync(TimeSpan idleFor, CancellationToken ct = default) =>
            Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void TagModelCatalog_ExposesDescriptionsForAllRegisteredModels()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"clipnest-models-{Guid.NewGuid():N}");
        var entries = TagModelDefinition.All
            .Select(def => new TagModelEntry(
                def,
                new TagModelManager(Path.Combine(tempRoot, def.Id), def),
                new StubTagger()))
            .ToList();
        var catalog = new TagModelCatalog(entries);

        var vm = new MainWindowViewModel(tagCatalog: catalog);

        Assert.Equal(TagModelDefinition.All.Count, vm.TagModels.Count);
        Assert.Equal(TagModelDefinition.Default.Id, vm.DefaultTagModelId);
        Assert.Contains(vm.TagModels, option => option.Id == TagModelDefinition.WdEva02LargeV3.Id);
        Assert.Contains(vm.TagModels, option => option.Id == TagModelDefinition.WdSwinV2V3.Id);
        Assert.Contains(vm.TagModels, option => option.Id == TagModelDefinition.ClTagger.Id);
        Assert.All(vm.TagModels, option => Assert.True(option.HasDescription));
    }
}
