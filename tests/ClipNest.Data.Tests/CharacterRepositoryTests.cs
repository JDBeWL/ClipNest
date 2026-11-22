using ClipNest.Data;
using ClipNest.Data.Repositories;
using Dapper;

namespace ClipNest.Data.Tests;

public sealed class CharacterRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly ClipNestDatabase _db;

    public CharacterRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clipnest-char-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new ClipNestDatabase(Path.Combine(_root, "clipnest.db"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    private async Task<long> SeedAssetAsync()
    {
        await using var connection = await _db.OpenAsync();
        var now = DateTime.UtcNow;

        await connection.ExecuteAsync(
            "INSERT INTO folders (path, watch_enabled, created_at) VALUES ('C:/pics', 1, @Now);",
            new { Now = now });
        var folderId = await connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();");

        await connection.ExecuteAsync("""
            INSERT INTO assets (sha256, file_name, created_at, updated_at)
            VALUES (@Sha, 'a.jpg', @Now, @Now);
            """, new { Sha = Guid.NewGuid().ToString("N"), Now = now });
        var id = await connection.ExecuteScalarAsync<long>("SELECT last_insert_rowid();");

        await connection.ExecuteAsync("""
            INSERT INTO asset_files (asset_id, folder_id, path, file_name, extension, size_bytes,
                                     modified_at, is_primary, file_state, created_at, updated_at)
            VALUES (@Id, @FolderId, 'C:/pics/a.jpg', 'a.jpg', '.jpg', 1, @Now, 1, 'ok', @Now, @Now);
            """, new { Id = id, FolderId = folderId, Now = now });
        return id;
    }

    [Fact]
    public void Normalize_TrimsCaseAndSpacing()
    {
        Assert.Equal("月村 手毬", CharacterRepository.Normalize("  月村   手毬 "));
        Assert.Equal("shino sawa hiro", CharacterRepository.Normalize("Shino  Sawa Hiro"));
    }

    [Fact]
    public async Task Ensure_CreatesOncePerNormalizedName()
    {
        await _db.InitializeAsync();
        var repository = new CharacterRepository(_db);

        var first = await repository.EnsureAsync("月村手毬");
        var second = await repository.EnsureAsync(" 月村手毬 ");

        Assert.Equal(first, second);
        Assert.Single(await repository.GetNamesAsync());
    }

    [Fact]
    public void SeriesFromName_ExtractsTrailingParenthetical()
    {
        Assert.Equal("girls' frontline", CharacterRepository.SeriesFromName("ump45 (girls' frontline)"));
        Assert.Equal("girls' frontline", CharacterRepository.SeriesFromName("ump45_(girls'_frontline)"));
        Assert.Equal("girls' frontline", CharacterRepository.SeriesFromName(
            "m4_sopmod_ii_(devourer_of_the_feast)_(girls'_frontline)"));
        Assert.Null(CharacterRepository.SeriesFromName("月村手毬"));
    }

    [Fact]
    public async Task Ensure_StoresSeriesFromCharacterName()
    {
        await _db.InitializeAsync();
        var repository = new CharacterRepository(_db);
        var assetId = await SeedAssetAsync();

        var id = await repository.EnsureAsync("ump45 (girls' frontline)");
        await repository.AssignUserAsync(id, new[] { assetId });

        await using var connection = await _db.OpenAsync();
        Assert.Equal("girls' frontline", await connection.ExecuteScalarAsync<string>(
            "SELECT series FROM characters WHERE id = @Id;", new { Id = id }));

        var match = (await repository.GetMatchesAsync(new[] { assetId }))[assetId];
        Assert.Equal("girls' frontline", match.Series);
    }

    [Fact]
    public async Task GetMatches_FallsBackToSeriesInName_WhenColumnEmpty()
    {
        await _db.InitializeAsync();
        var repository = new CharacterRepository(_db);
        var assetId = await SeedAssetAsync();

        // 模拟旧数据，角色名带作品，但 series 列尚未写入。
        var id = await repository.EnsureAsync("ump45 (girls' frontline)");
        await using (var connection = await _db.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE characters SET series = NULL WHERE id = @Id;", new { Id = id });
        }

        await repository.AssignUserAsync(id, new[] { assetId });

        var match = (await repository.GetMatchesAsync(new[] { assetId }))[assetId];
        Assert.Equal("girls' frontline", match.Series);
    }

    [Fact]
    public async Task AssignUser_OverridesExistingAssignment()
    {
        await _db.InitializeAsync();
        var repository = new CharacterRepository(_db);
        var assetId = await SeedAssetAsync();

        var hiro = await repository.EnsureAsync("篠澤広");
        var temari = await repository.EnsureAsync("月村手毬");
        await repository.AssignUserAsync(hiro, new[] { assetId });
        await repository.AssignUserAsync(temari, new[] { assetId });

        var match = (await repository.GetMatchesAsync(new[] { assetId }))[assetId];
        Assert.Equal(temari, match.CharacterId);
        Assert.Equal("user", match.Source);
    }

    [Fact]
    public async Task AssignAuto_DoesNotOverwriteUserChoice()
    {
        await _db.InitializeAsync();
        var repository = new CharacterRepository(_db);
        var assetId = await SeedAssetAsync();

        var hiro = await repository.EnsureAsync("篠澤広");
        var temari = await repository.EnsureAsync("月村手毬");
        await repository.AssignUserAsync(hiro, new[] { assetId });
        await repository.AssignAutoAsync(temari, new[] { (assetId, 0.9f) });

        var match = (await repository.GetMatchesAsync(new[] { assetId }))[assetId];
        Assert.Equal(hiro, match.CharacterId);
    }

    [Fact]
    public async Task Prototype_RoundTripsThroughFloat16()
    {
        await _db.InitializeAsync();
        var repository = new CharacterRepository(_db);
        var characterId = await repository.EnsureAsync("月村手毬");

        var vector = new float[512];
        vector[0] = 1f;
        await repository.SavePrototypeAsync(characterId, "test-model", vector, 3);

        var prototype = Assert.Single(await repository.GetPrototypesAsync("test-model"));
        Assert.Equal(512, prototype.Vector.Length);
        Assert.Equal(1f, prototype.Vector[0], 3);
        Assert.Equal(3, prototype.SampleCount);
    }
}
