using ClipNest.Ai.Models;
using ClipNest.Core.Embedding;
using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;

namespace ClipNest.Ai.Tests;

public sealed class EmbeddingQueueTests
{
    private static async Task<(ClipNestDatabase Db, string Path)> CreateDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-queue-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        return (db, path);
    }

    private static async Task AddAssetAsync(ClipNestDatabase db, string hash, string fileName)
    {
        const string folderPath = @"C:\photos";
        var folderId = await new FolderRepository(db).EnsureAsync(folderPath);
        var now = DateTime.UtcNow;

        await new AssetRepository(db).UpsertAsync(
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
    public async Task ProcessPending_WhenPaused_LeavesAssetsPending()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            await AddAssetAsync(db, "hash-paused", "a.jpg");

            var repository = new EmbeddingRepository(db);
            using var queue = new EmbeddingQueue(
                repository, new FakeEncoder(true), ClipModelDefinition.ChineseClipVitB16)
            {
                IsPaused = true
            };

            await queue.ProcessPendingAsync();

            var stats = await repository.GetStatsAsync();
            Assert.Equal(1, stats.Pending);
            Assert.Equal(0, stats.Skipped);
            Assert.Equal(0, stats.Ready);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessPending_WithWorkingEncoder_MarksAllReady()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            await AddAssetAsync(db, "hash-1", "a.jpg");
            await AddAssetAsync(db, "hash-2", "b.jpg");

            var repository = new EmbeddingRepository(db);
            var progressCount = 0;
            using var queue = new EmbeddingQueue(
                repository, new FakeEncoder(true), ClipModelDefinition.ChineseClipVitB16);
            queue.ProgressChanged += (_, _) => progressCount++;

            await queue.ProcessPendingAsync();

            var stats = await repository.GetStatsAsync();
            Assert.Equal(2, stats.Ready);
            Assert.Equal(0, stats.Pending);
            Assert.Empty(await repository.GetPendingAsync());
            Assert.True(progressCount >= 1);

            var vectors = await repository.GetAllVectorsAsync(ClipModelDefinition.ChineseClipVitB16.Id);
            Assert.Equal(2, vectors.Count);
            foreach (var vector in vectors.Values)
            {
                Assert.InRange(vector[0], 0.99f, 1.01f);
                Assert.InRange(vector[1], -0.01f, 0.01f);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessPending_WithoutModel_MarksSkipped()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            await AddAssetAsync(db, "hash-1", "a.jpg");
            await AddAssetAsync(db, "hash-2", "b.jpg");

            var repository = new EmbeddingRepository(db);
            using var queue = new EmbeddingQueue(
                repository, new FakeEncoder(false), ClipModelDefinition.ChineseClipVitB16);

            await queue.ProcessPendingAsync();

            var stats = await repository.GetStatsAsync();
            Assert.Equal(2, stats.Skipped);
            Assert.Equal(0, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ProcessPending_WithFailingEncoder_MarksError()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            await AddAssetAsync(db, "hash-1", "a.jpg");

            var repository = new EmbeddingRepository(db);
            using var queue = new EmbeddingQueue(
                repository, new FakeEncoder(true, failImage: true), ClipModelDefinition.ChineseClipVitB16);

            await queue.ProcessPendingAsync();

            var stats = await repository.GetStatsAsync();
            Assert.Equal(1, stats.Error);
            Assert.Equal(0, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeEncoder : IClipEncoder
    {
        private readonly bool _failImage;

        public FakeEncoder(bool available, bool failImage = false)
        {
            IsAvailable = available;
            _failImage = failImage;
        }

        public bool IsAvailable { get; }

        public Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new[] { 1f, 0f });

        public Task<float[]> EncodeImageAsync(string path, CancellationToken ct = default) =>
            _failImage
                ? throw new InvalidOperationException("模拟编码失败")
                : Task.FromResult(new[] { 1f, 0f });

        public Task ReleaseAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
