using ClipNest.Core.Embedding;
using ClipNest.Core.Models;
using ClipNest.Core.Search;
using ClipNest.Data.Repositories;
using ClipNest.Data.Search;

namespace ClipNest.Data.Tests;

public sealed class SemanticSearchTests
{
    private const string ModelId = "chinese-clip-vit-base-patch16";

    private sealed class FakeEncoder : IClipEncoder
    {
        public bool IsAvailable { get; set; } = true;

        public float[] TextVector { get; set; } = { 1f, 0f };

        public string? LastText { get; private set; }

        public Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default)
        {
            LastText = text;
            return Task.FromResult(TextVector);
        }

        public Task<float[]> EncodeImageAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(TextVector);

        public Task ReleaseAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<(ClipNestDatabase Db, string Path)> CreateDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-semantic-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        return (db, path);
    }

    private static async Task<long> AddAssetAsync(ClipNestDatabase db, string hash, string fileName)
    {
        var folderId = await new FolderRepository(db).EnsureAsync(@"C:\photos");
        var now = DateTime.UtcNow;

        return await new AssetRepository(db).UpsertAsync(
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
                Path = Path.Combine(@"C:\photos", fileName),
                FileName = fileName,
                Extension = ".jpg",
                SizeBytes = 10,
                ModifiedAt = now,
                IsPrimary = true
            });
    }

    private static AssetSearchService CreateService(ClipNestDatabase db, IClipEncoder encoder) =>
        new(
            new TagRepository(db),
            new SearchRepository(db),
            new SemanticSearchService(
                new EmbeddingRepository(db), new SearchRepository(db), encoder, ModelId));

    [Fact]
    public async Task Search_Semantic_ReturnsMostSimilarFirst()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var a = await AddAssetAsync(db, "hash-a", "a.jpg");
            var b = await AddAssetAsync(db, "hash-b", "b.jpg");

            var embeddings = new EmbeddingRepository(db);
            await embeddings.UpsertAsync(a, ModelId, new[] { 1f, 0f });
            await embeddings.UpsertAsync(b, ModelId, new[] { 0f, 1f });

            var service = CreateService(db, new FakeEncoder { TextVector = new[] { 1f, 0f } });
            var result = await service.SearchAsync("sim:\"x\"");

            Assert.Equal(2, result.Count);
            Assert.Equal(a, result[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_Semantic_WithTagFilter_OnlyReturnsTaggedAsset()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var a = await AddAssetAsync(db, "hash-a", "a.jpg");
            var b = await AddAssetAsync(db, "hash-b", "b.jpg");

            var embeddings = new EmbeddingRepository(db);
            await embeddings.UpsertAsync(a, ModelId, new[] { 1f, 0f });
            await embeddings.UpsertAsync(b, ModelId, new[] { 0f, 1f });

            var tags = new TagRepository(db);
            var travel = await tags.CreateAsync("旅行", null);
            await tags.AssignAsync(b, travel);

            var service = CreateService(db, new FakeEncoder { TextVector = new[] { 1f, 0f } });
            // B 的相似度更低，但只有 B 带「旅行」标签。
            var result = await service.SearchAsync("#旅行 sim:\"x\"");

            Assert.Single(result);
            Assert.Equal(b, result[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_Semantic_OrCombination_Throws()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var service = CreateService(db, new FakeEncoder());
            await Assert.ThrowsAsync<SearchParseException>(
                () => service.SearchAsync("sim:\"x\" | #旅行"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_Semantic_WithoutModel_Throws()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var service = CreateService(db, new FakeEncoder { IsAvailable = false });
            var ex = await Assert.ThrowsAsync<SearchParseException>(
                () => service.SearchAsync("sim:\"x\""));
            Assert.Contains("模型未安装", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RankAsync_ScoresCandidatesSubsetAndAppliesLimit()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var a = await AddAssetAsync(db, "hash-a", "a.jpg");
            var b = await AddAssetAsync(db, "hash-b", "b.jpg");
            // C 没有向量，不应出现在排序结果里。
            await AddAssetAsync(db, "hash-c", "c.jpg");

            var embeddings = new EmbeddingRepository(db);
            await embeddings.UpsertAsync(a, ModelId, new[] { 1f, 0f });
            await embeddings.UpsertAsync(b, ModelId, new[] { 0f, 1f });

            var semantic = new SemanticSearchService(
                embeddings, new SearchRepository(db), new FakeEncoder { TextVector = new[] { 1f, 0f } }, ModelId);
            var candidates = await new SearchRepository(db).QueryAsync(
                SearchSqlCompiler.Compile(
                    new MatchAllNode(), new Dictionary<string, IReadOnlyList<long>>()),
                100);

            var ranked = await semantic.RankAsync("x", candidates, 10);
            Assert.Equal(new[] { a, b }, ranked.Select(item => item.Id).ToArray());

            var top = await semantic.RankAsync("x", candidates, 1);
            Assert.Single(top);
            Assert.Equal(a, top[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
