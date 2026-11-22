using ClipNest.Core.Indexing;
using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using ClipNest.Imaging.Indexing;

namespace ClipNest.Imaging.Tests;

public sealed class FileScannerTests
{
    private sealed class FakeMetadataReader : IFileMetadataReader
    {
        public Task<AssetMetadata> ReadAsync(string path, CancellationToken ct = default)
            => Task.FromResult(AssetMetadata.Empty);
    }

    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = new();

        public void Report(ScanProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Scan_Reports_Progress_Up_To_Total()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(root, $"{i}.jpg"), new[] { (byte)i });
            }

            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var scanner = new FileScanner(
                new FolderRepository(db), new AssetRepository(db), new FakeMetadataReader());
            var progress = new CollectingProgress();

            Assert.Equal(10, await scanner.ScanAsync(root, progress));
            Assert.Equal(0, progress.Reports[0].Processed);
            Assert.Equal(10, progress.Reports[0].Total);
            Assert.Equal(10, progress.Reports[^1].Processed);
            Assert.Equal(10, progress.Reports[^1].Total);
            Assert.All(
                progress.Reports.Zip(progress.Reports.Skip(1)),
                pair => Assert.True(pair.First.Processed <= pair.Second.Processed));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Scan_DuplicateContent_CreatesOneAssetTwoFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "a.jpg"), new byte[] { 1, 2, 3 });
            await File.WriteAllBytesAsync(Path.Combine(root, "b.jpg"), new byte[] { 1, 2, 3 });

            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var scanner = new FileScanner(folders, assets, new FakeMetadataReader());

            var processed = await scanner.ScanAsync(root);
            Assert.Equal(2, processed);

            var folderId = await folders.EnsureAsync(root);
            var files = await assets.GetFilesByFolderAsync(folderId);
            Assert.Equal(2, files.Count);
            Assert.Single(files.Select(f => f.AssetId).Distinct());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Rescan_DeletedFolder_MarksAllFilesMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-scan-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"clipnest-scan-db-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "a.jpg"), new byte[] { 1, 2, 3 });

            var db = new ClipNestDatabase(dbPath);
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var scanner = new FileScanner(folders, assets, new FakeMetadataReader());

            Assert.Equal(1, await scanner.ScanAsync(root));

            // 整个文件夹被删除后刷新，所有文件都应变成 Missing。
            Directory.Delete(root, true);
            Assert.Equal(0, await scanner.ScanAsync(root));

            var folderId = await folders.EnsureAsync(root);
            var files = await assets.GetFilesByFolderAsync(folderId);
            Assert.All(files, file => Assert.Equal(FileState.Missing, file.State));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Rescan_SkipsUnchanged_And_MarksMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a.jpg");
            var b = Path.Combine(root, "b.jpg");
            await File.WriteAllBytesAsync(a, new byte[] { 1 });
            await File.WriteAllBytesAsync(b, new byte[] { 2 });

            var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
            await db.InitializeAsync();
            var folders = new FolderRepository(db);
            var assets = new AssetRepository(db);
            var scanner = new FileScanner(folders, assets, new FakeMetadataReader());

            Assert.Equal(2, await scanner.ScanAsync(root));
            Assert.Equal(0, await scanner.ScanAsync(root));

            File.Delete(b);
            await scanner.ScanAsync(root);

            var folderId = await folders.EnsureAsync(root);
            var files = await assets.GetFilesByFolderAsync(folderId);
            Assert.Contains(files, f => f.FileName == "b.jpg" && f.State == FileState.Missing);
            Assert.Contains(files, f => f.FileName == "a.jpg" && f.State == FileState.Indexed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
