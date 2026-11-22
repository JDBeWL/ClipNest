using System.Security.Cryptography;
using System.Text;
using ClipNest.App.Services;
using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using ClipNest.Data.Search;

namespace ClipNest.App.Tests;

public sealed class AssetFileOperationsTests
{
    private sealed class DeletingRemover : IFileRemover
    {
        public IReadOnlyList<string> Remove(IReadOnlyList<string> paths)
        {
            foreach (var path in paths.Where(File.Exists)) File.Delete(path);
            return paths.Where(path => !File.Exists(path)).ToList();
        }
    }

    private sealed class BlockingRemover : IFileRemover
    {
        public IReadOnlyList<string> Remove(IReadOnlyList<string> paths) => Array.Empty<string>();
    }

    private sealed class Library : IDisposable
    {
        public Library(string root, ClipNestDatabase db, FolderRepository folders, AssetRepository assets)
        {
            Root = root;
            Db = db;
            Folders = folders;
            Assets = assets;
            Inbox = Path.Combine(root, "inbox");
            Sorted = Path.Combine(root, "sorted");
        }

        public string Root { get; }
        public string Inbox { get; }
        public string Sorted { get; }
        public ClipNestDatabase Db { get; }
        public FolderRepository Folders { get; }
        public AssetRepository Assets { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响断言结果。
            }
        }
    }

    private static async Task<Library> CreateLibraryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-ops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "sorted"));

        var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
        await db.InitializeAsync();
        return new Library(root, db, new FolderRepository(db), new AssetRepository(db));
    }

    private static async Task<AssetListItem> AddAssetAsync(
        Library library, string folder, string fileName, string content)
    {
        var path = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(path, content);

        var folderId = await library.Folders.EnsureAsync(folder);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
        var info = new FileInfo(path);
        var now = DateTime.UtcNow;

        var id = await library.Assets.UpsertAsync(
            new Asset
            {
                Sha256 = hash,
                FileName = fileName,
                Width = 100,
                Height = 100,
                CreatedAt = now,
                UpdatedAt = now
            },
            new AssetFile
            {
                FolderId = folderId,
                Path = path,
                FileName = fileName,
                Extension = Path.GetExtension(fileName),
                SizeBytes = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                IsPrimary = true
            });

        return new AssetListItem
        {
            Id = id,
            Sha256 = hash,
            FileName = fileName,
            PrimaryPath = path
        };
    }

    [Fact]
    public async Task Move_RelocatesFileAndUpdatesIndex()
    {
        using var library = await CreateLibraryAsync();
        var item = await AddAssetAsync(library, library.Inbox, "a.jpg", "alpha");
        var targetId = await library.Folders.EnsureAsync(library.Sorted);
        var operations = new AssetFileOperations(
            library.Folders, library.Assets, new DeletingRemover());

        var outcome = await operations.TransferAsync(
            new[] { item }, library.Sorted, FileTransferMode.Move);

        Assert.Equal(1, outcome.Succeeded);
        Assert.False(File.Exists(Path.Combine(library.Inbox, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(library.Sorted, "a.jpg")));

        var files = await library.Assets.GetFilesForAssetsAsync(new[] { item.Id });
        var file = Assert.Single(files);
        Assert.Equal(Path.Combine(library.Sorted, "a.jpg"), file.Path);
        Assert.Equal(targetId, file.FolderId);
        Assert.True(file.IsPrimary);
    }

    [Fact]
    public async Task Copy_KeepsSourceAndRegistersSecondaryFile()
    {
        using var library = await CreateLibraryAsync();
        var item = await AddAssetAsync(library, library.Inbox, "a.jpg", "alpha");
        var targetId = await library.Folders.EnsureAsync(library.Sorted);
        var operations = new AssetFileOperations(
            library.Folders, library.Assets, new DeletingRemover());

        var outcome = await operations.TransferAsync(
            new[] { item }, library.Sorted, FileTransferMode.Copy);

        Assert.Equal(1, outcome.Succeeded);
        Assert.True(File.Exists(Path.Combine(library.Inbox, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(library.Sorted, "a.jpg")));

        var files = await library.Assets.GetFilesForAssetsAsync(new[] { item.Id });
        Assert.Equal(2, files.Count);
        var primary = Assert.Single(files, file => file.IsPrimary);
        Assert.Equal(Path.Combine(library.Inbox, "a.jpg"), primary.Path);
        Assert.Contains(
            files,
            file => !file.IsPrimary && file.FolderId == targetId &&
                file.Path == Path.Combine(library.Sorted, "a.jpg"));
    }

    [Fact]
    public async Task Transfer_AvoidsOverwritingExistingFile()
    {
        using var library = await CreateLibraryAsync();
        var item = await AddAssetAsync(library, library.Inbox, "a.jpg", "alpha");
        await File.WriteAllTextAsync(Path.Combine(library.Sorted, "a.jpg"), "existing");
        var operations = new AssetFileOperations(
            library.Folders, library.Assets, new DeletingRemover());

        var outcome = await operations.TransferAsync(
            new[] { item }, library.Sorted, FileTransferMode.Copy);

        Assert.Equal(1, outcome.Succeeded);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(library.Sorted, "a.jpg")));
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(library.Sorted, "a (1).jpg")));
    }

    [Fact]
    public async Task Move_IntoSourceFolderIsSkipped()
    {
        using var library = await CreateLibraryAsync();
        var item = await AddAssetAsync(library, library.Inbox, "a.jpg", "alpha");
        var operations = new AssetFileOperations(
            library.Folders, library.Assets, new DeletingRemover());

        var outcome = await operations.TransferAsync(
            new[] { item }, library.Inbox, FileTransferMode.Move);

        Assert.Equal(0, outcome.Succeeded);
        Assert.Equal(1, outcome.Skipped);
        Assert.False(outcome.HasErrors);
        Assert.True(File.Exists(Path.Combine(library.Inbox, "a.jpg")));
    }

    [Fact]
    public async Task Delete_RemovesFilesAndIndex()
    {
        using var library = await CreateLibraryAsync();
        var item = await AddAssetAsync(library, library.Inbox, "a.jpg", "alpha");
        var operations = new AssetFileOperations(
            library.Folders, library.Assets, new DeletingRemover());

        var outcome = await operations.DeleteAsync(new[] { item });

        Assert.Equal(1, outcome.Succeeded);
        Assert.False(File.Exists(Path.Combine(library.Inbox, "a.jpg")));
        Assert.Empty(await library.Assets.GetFilesForAssetsAsync(new[] { item.Id }));

        var remaining = await new SearchRepository(library.Db).GetByIdsAsync(new[] { item.Id });
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Delete_KeepsIndexWhenFileStaysOnDisk()
    {
        using var library = await CreateLibraryAsync();
        var item = await AddAssetAsync(library, library.Inbox, "a.jpg", "alpha");
        var operations = new AssetFileOperations(
            library.Folders, library.Assets, new BlockingRemover());

        var outcome = await operations.DeleteAsync(new[] { item });

        Assert.Equal(0, outcome.Succeeded);
        Assert.True(outcome.HasErrors);
        Assert.True(File.Exists(Path.Combine(library.Inbox, "a.jpg")));
        Assert.Single(await library.Assets.GetFilesForAssetsAsync(new[] { item.Id }));
    }
}
