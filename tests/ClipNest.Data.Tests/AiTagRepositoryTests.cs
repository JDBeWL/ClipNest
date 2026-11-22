using ClipNest.Core.Models;
using ClipNest.Core.Tagging;
using ClipNest.Data.Repositories;
using Dapper;

namespace ClipNest.Data.Tests;

public sealed class AiTagRepositoryTests
{
    private const string ModelId = "camie-tagger";

    private static async Task<(ClipNestDatabase Db, string Path)> CreateDbAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-ai-tag-{Guid.NewGuid():N}.db");
        var db = new ClipNestDatabase(path);
        await db.InitializeAsync();
        return (db, path);
    }

    private static async Task<long> AddAssetAsync(ClipNestDatabase db, string hash, string fileName)
        => (await AddAssetInFolderAsync(db, @"C:\photos", hash, fileName)).AssetId;

    private static async Task<(long FolderId, long AssetId)> AddAssetInFolderAsync(
        ClipNestDatabase db, string folderPath, string hash, string fileName)
    {
        var folderId = await new FolderRepository(db).EnsureAsync(folderPath);
        var now = DateTime.UtcNow;

        var assetId = await new AssetRepository(db).UpsertAsync(
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

        return (folderId, assetId);
    }

    private static AiTagResult CreateResult()
    {
        return new AiTagResult(
            new[]
            {
                new AiTag("fujita_kotone", AiTagCategory.Character, 0.30f),
                new AiTag("long_hair", AiTagCategory.General, 0.62f)
            },
            new[]
            {
                new AiTagScore(7, "long_hair", AiTagCategory.General, 0.62f),
                new AiTagScore(12, "fujita_kotone", AiTagCategory.Character, 0.30f),
                new AiTagScore(31, "smile", AiTagCategory.General, 0.11f)
            });
    }

    [Fact]
    public async Task Replace_PersistsTagsScoresAndCatalog()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());

            await using var connection = await db.OpenAsync();

            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE asset_id = @AssetId AND model_id = @ModelId;",
                new { AssetId = assetId, ModelId }));

            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_scores WHERE asset_id = @AssetId AND model_id = @ModelId;",
                new { AssetId = assetId, ModelId }));

            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ai_tag_catalog WHERE model_id = @ModelId;",
                new { ModelId }));

            Assert.Equal("smile", await connection.ExecuteScalarAsync<string>(
                "SELECT name FROM ai_tag_catalog WHERE model_id = @ModelId AND tag_index = 31;",
                new { ModelId }));

            Assert.Equal(AiTagCategory.Character, await connection.ExecuteScalarAsync<string>(
                "SELECT category FROM ai_tag_catalog WHERE model_id = @ModelId AND tag_index = 12;",
                new { ModelId }));

            var score = await connection.ExecuteScalarAsync<double>(
                "SELECT score FROM asset_ai_tag_scores WHERE asset_id = @AssetId AND model_id = @ModelId AND tag_index = 31;",
                new { AssetId = assetId, ModelId });
            Assert.InRange(score, 0.109, 0.111);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Replace_SecondRun_DropsStaleScores_ButKeepsCatalog()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());
            await repo.ReplaceAsync(assetId, ModelId, new AiTagResult(
                Array.Empty<AiTag>(),
                new[] { new AiTagScore(99, "new_tag", AiTagCategory.General, 0.50f) }));

            await using var connection = await db.OpenAsync();

            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE asset_id = @AssetId AND model_id = @ModelId;",
                new { AssetId = assetId, ModelId }));

            Assert.Equal(99, await connection.ExecuteScalarAsync<int>(
                "SELECT tag_index FROM asset_ai_tag_scores WHERE asset_id = @AssetId AND model_id = @ModelId;",
                new { AssetId = assetId, ModelId }));

            // 词表是共享的，旧下标仍保留，避免后续筛选时缺名字。
            Assert.Equal(4, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ai_tag_catalog WHERE model_id = @ModelId;",
                new { ModelId }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Pending_UsesCompletionMarker_AndBackfillsLegacyTags()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var legacyAsset = await AddAssetAsync(db, "hash-1", "a.jpg");
            var newAsset = await AddAssetAsync(db, "hash-2", "b.jpg");
            var repo = new AiTagRepository(db);

            // 模拟迁移 004 之前的库，只有结论标签，没有分数快照与完成标记。
            await using (var connection = await db.OpenAsync())
            {
                await connection.ExecuteAsync("""
                    INSERT INTO asset_ai_tags (asset_id, tag, category, score, model_id, created_at)
                    VALUES (@AssetId, 'legacy_tag', @Category, 0.5, @ModelId, @CreatedAt);
                    """, new
                {
                    AssetId = legacyAsset,
                    Category = AiTagCategory.General,
                    ModelId,
                    CreatedAt = DateTime.UtcNow
                });
            }

            var pending = await repo.GetPendingAsync(ModelId, 10);
            Assert.Equal(
                new[] { legacyAsset, newAsset },
                pending.Select(job => job.AssetId).ToArray());

            await repo.ReplaceAsync(legacyAsset, ModelId, AiTagResult.Empty);

            pending = await repo.GetPendingAsync(ModelId, 10);
            Assert.Equal(new[] { newAsset }, pending.Select(job => job.AssetId).ToArray());

            var stats = await repo.GetStatsAsync(ModelId);
            Assert.Equal(1, stats.Done);
            Assert.Equal(1, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Pending_InvalidatesRunsFromOlderPipelineVersion()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());
            Assert.Empty(await repo.GetPendingAsync(ModelId, 10));

            await using (var connection = await db.OpenAsync())
            {
                await connection.ExecuteAsync(
                    "UPDATE asset_ai_tag_runs SET pipeline_version = @Old WHERE asset_id = @AssetId;",
                    new { Old = TaggingPipeline.Version - 1, AssetId = assetId });
            }

            Assert.Single(await repo.GetPendingAsync(ModelId, 10));

            var stats = await repo.GetStatsAsync(ModelId);
            Assert.Equal(0, stats.Done);
            Assert.Equal(1, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Reset_ClearsAiResults_ButKeepsManualTags()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);
            await repo.ReplaceAsync(assetId, ModelId, CreateResult());

            var tags = new TagRepository(db);
            var manualTagId = await tags.GetOrCreateAsync("my_tag");
            await tags.AssignAsync(assetId, manualTagId);

            await repo.ResetAsync(ModelId);

            await using var connection = await db.OpenAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE model_id = @ModelId;", new { ModelId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_scores WHERE model_id = @ModelId;", new { ModelId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_runs WHERE model_id = @ModelId;", new { ModelId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_tags WHERE asset_id = @AssetId AND source = 'ai';",
                new { AssetId = assetId }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_tags WHERE asset_id = @AssetId AND source = 'manual';",
                new { AssetId = assetId }));

            Assert.Contains(await repo.GetPendingAsync(ModelId, 10), job => job.AssetId == assetId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FolderModel_SelectsWhichModelReceivesAssets()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var (folderA, assetA) = await AddAssetInFolderAsync(db, @"C:\a", "hash-a", "a.jpg");
            var (folderB, assetB) = await AddAssetInFolderAsync(db, @"C:\b", "hash-b", "b.jpg");
            Assert.NotEqual(folderA, folderB);

            var folders = new FolderRepository(db);
            await folders.SetTagModelAsync(folderB, "camie-tagger-v2");

            var repo = new AiTagRepository(db);
            var defaultPending = await repo.GetPendingAsync(ModelId, ModelId, 10);
            Assert.Contains(defaultPending, job => job.AssetId == assetA);
            Assert.DoesNotContain(defaultPending, job => job.AssetId == assetB);

            var v2Pending = await repo.GetPendingAsync("camie-tagger-v2", ModelId, 10);
            Assert.Contains(v2Pending, job => job.AssetId == assetB);
            Assert.DoesNotContain(v2Pending, job => job.AssetId == assetA);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ResetForFolder_ClearsOnlyThatFolder()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var (_, assetA) = await AddAssetInFolderAsync(db, @"C:\a", "hash-a", "a.jpg");
            var (folderB, assetB) = await AddAssetInFolderAsync(db, @"C:\b", "hash-b", "b.jpg");

            var repo = new AiTagRepository(db);
            await repo.ReplaceAsync(assetA, ModelId, CreateResult());
            await repo.ReplaceAsync(assetB, ModelId, CreateResult());

            await repo.ResetForFolderAsync(folderB);

            await using var connection = await db.OpenAsync();
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE asset_id = @AssetId;",
                new { AssetId = assetA }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE asset_id = @AssetId;",
                new { AssetId = assetB }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_runs WHERE asset_id = @AssetId;",
                new { AssetId = assetA }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_runs WHERE asset_id = @AssetId;",
                new { AssetId = assetB }));
            Assert.NotEqual(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_tags WHERE asset_id = @AssetId AND source = 'ai';",
                new { AssetId = assetA }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_tags WHERE asset_id = @AssetId AND source = 'ai';",
                new { AssetId = assetB }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ResetForAsset_ClearsOnlyThatAsset()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetA = await AddAssetAsync(db, "hash-a", "a.jpg");
            var assetB = await AddAssetAsync(db, "hash-b", "b.jpg");

            var repo = new AiTagRepository(db);
            await repo.ReplaceAsync(assetA, ModelId, CreateResult());
            await repo.ReplaceAsync(assetB, ModelId, CreateResult());

            await repo.ResetForAssetAsync(assetA);

            await using var connection = await db.OpenAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE asset_id = @AssetId;",
                new { AssetId = assetA }));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tags WHERE asset_id = @AssetId;",
                new { AssetId = assetB }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_runs WHERE asset_id = @AssetId;",
                new { AssetId = assetA }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_ai_tag_runs WHERE asset_id = @AssetId;",
                new { AssetId = assetB }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Reanalyze_ClearsManualCharacterOverride()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);
            var tags = new TagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());
            await tags.ReplaceCategoryTagsAsync(
                new[] { assetId }, AiTagCategory.Character, new[] { "manual name" });

            await repo.ResetForAssetAsync(assetId);
            await repo.ReplaceAsync(assetId, ModelId, CreateResult());

            var characters = await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Character);
            Assert.Contains(characters, tag => tag.Name == "fujita kotone");
            Assert.DoesNotContain(characters, tag => tag.Name == "manual name");

            await using var connection = await db.OpenAsync();
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM asset_category_overrides WHERE asset_id = @AssetId;",
                new { AssetId = assetId }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetModelIdForAsset_UsesFolderOverride()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var (_, assetA) = await AddAssetInFolderAsync(db, @"C:\a", "hash-a", "a.jpg");
            var (folderB, assetB) = await AddAssetInFolderAsync(db, @"C:\b", "hash-b", "b.jpg");
            await new FolderRepository(db).SetTagModelAsync(folderB, "camie-tagger-v2");

            var repo = new AiTagRepository(db);
            Assert.Equal(ModelId, await repo.GetModelIdForAssetAsync(assetA, ModelId));
            Assert.Equal("camie-tagger-v2", await repo.GetModelIdForAssetAsync(assetB, ModelId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Stats_FollowFolderModelAssignment()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var (_, assetA) = await AddAssetInFolderAsync(db, @"C:\a", "hash-a", "a.jpg");
            var (folderB, _) = await AddAssetInFolderAsync(db, @"C:\b", "hash-b", "b.jpg");
            await new FolderRepository(db).SetTagModelAsync(folderB, "camie-tagger-v2");

            var repo = new AiTagRepository(db);
            await repo.ReplaceAsync(assetA, ModelId, CreateResult());

            var stats = await repo.GetStatsAsync(ModelId);
            Assert.Equal(1, stats.Done);
            Assert.Equal(1, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetForAsset_HidesResultsFromOlderPipelineVersion()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());
            var tags = await repo.GetForAssetAsync(assetId, ModelId);
            Assert.Equal(2, tags.Count);

            await using (var connection = await db.OpenAsync())
            {
                await connection.ExecuteAsync(
                    "UPDATE asset_ai_tag_runs SET pipeline_version = @Old WHERE asset_id = @AssetId;",
                    new { Old = TaggingPipeline.Version - 1, AssetId = assetId });
            }

            Assert.Empty(await repo.GetForAssetAsync(assetId, ModelId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Replace_WritesRecognitionResultIntoTagSystem()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);
            var tags = new TagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());

            var assigned = await tags.GetTagsForAssetAsync(assetId);
            Assert.Equal(
                new[] { "fujita kotone", "long hair" },
                assigned.Select(tag => tag.Name).OrderBy(name => name).ToArray());

            await using var connection = await db.OpenAsync();
            var sources = (await connection.QueryAsync<string>(
                "SELECT source FROM asset_tags WHERE asset_id = @AssetId;",
                new { AssetId = assetId })).ToList();
            Assert.NotEmpty(sources);
            Assert.All(sources, source => Assert.Equal("ai", source));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Replace_ReplacesAiTags_ButKeepsManualOnes()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var repo = new AiTagRepository(db);
            var tags = new TagRepository(db);

            await repo.ReplaceAsync(assetId, ModelId, CreateResult());

            // 用户手动加上同名标签后，来源升级为 manual，重识别不应把它清掉。
            var longHairId = await tags.GetOrCreateAsync("long hair");
            await tags.AssignAsync(assetId, longHairId);

            await repo.ReplaceAsync(assetId, ModelId, new AiTagResult(
                new[] { new AiTag("smile", AiTagCategory.General, 0.50f) },
                Array.Empty<AiTagScore>()));

            var assigned = await tags.GetTagsForAssetAsync(assetId);
            Assert.Equal(
                new[] { "long hair", "smile" },
                assigned.Select(tag => tag.Name).OrderBy(name => name).ToArray());

            await using var connection = await db.OpenAsync();
            Assert.Equal("manual", await connection.ExecuteScalarAsync<string>(
                "SELECT source FROM asset_tags WHERE asset_id = @AssetId AND tag_id = @TagId;",
                new { AssetId = assetId, TagId = longHairId }));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplaceCategoryTags_ReplacesCharactersAndSeries()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var first = await AddAssetAsync(db, "hash-1", "a.jpg");
            var second = await AddAssetAsync(db, "hash-2", "b.jpg");
            var tags = new TagRepository(db);

            await tags.ReplaceCategoryTagsAsync(
                new[] { first, second },
                AiTagCategory.Character,
                new[] { "月村手毬", "藤田琴音" });

            var characters = await tags.GetTagsByCategoryAsync(first, AiTagCategory.Character);
            Assert.Equal(2, characters.Count);

            await tags.ReplaceCategoryTagsAsync(
                new[] { first },
                AiTagCategory.Character,
                new[] { "十王星南" });

            characters = await tags.GetTagsByCategoryAsync(first, AiTagCategory.Character);
            var character = Assert.Single(characters);
            Assert.Equal("十王星南", character.Name);

            await tags.ReplaceCategoryTagsAsync(
                new[] { first }, AiTagCategory.Character, Array.Empty<string>());
            Assert.Empty(await tags.GetTagsByCategoryAsync(first, AiTagCategory.Character));

            // 清空只影响这一张图。
            Assert.Equal(2, (await tags.GetTagsByCategoryAsync(second, AiTagCategory.Character)).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Replace_DoesNotOverwriteManualCharactersOrSeries()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var aiTags = new AiTagRepository(db);
            var tags = new TagRepository(db);

            var result = new AiTagResult(
                new[]
                {
                    new AiTag("fujita_kotone", AiTagCategory.Character, 0.50f),
                    new AiTag("gakuen_idolmaster", AiTagCategory.Series, 0.50f),
                    new AiTag("long_hair", AiTagCategory.General, 0.62f)
                },
                Array.Empty<AiTagScore>());

            await aiTags.ReplaceAsync(assetId, ModelId, result);

            // 用户修改 AI 识别出的角色与作品。
            await tags.ReplaceCategoryTagsAsync(
                new[] { assetId }, AiTagCategory.Character, new[] { "月村手毬" });
            await tags.ReplaceCategoryTagsAsync(
                new[] { assetId }, AiTagCategory.Series, new[] { "学园偶像大师" });

            await aiTags.ReplaceAsync(assetId, ModelId, result);

            var characters = await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Character);
            var character = Assert.Single(characters);
            Assert.Equal("月村手毬", character.Name);

            var series = await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Series);
            var work = Assert.Single(series);
            Assert.Equal("学园偶像大师", work.Name);

            // 普通特征标签仍跟随 AI 结果。
            var all = await tags.GetTagsForAssetAsync(assetId);
            Assert.Contains(all, tag => tag.Name == "long hair");
            Assert.DoesNotContain(all, tag => tag.Name == "fujita kotone");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Replace_DoesNotRestoreCharactersClearedByUser()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            var aiTags = new AiTagRepository(db);
            var tags = new TagRepository(db);

            var result = new AiTagResult(
                new[]
                {
                    new AiTag("fujita_kotone", AiTagCategory.Character, 0.50f),
                    new AiTag("gakuen_idolmaster", AiTagCategory.Series, 0.50f)
                },
                Array.Empty<AiTagScore>());

            await aiTags.ReplaceAsync(assetId, ModelId, result);

            // 清空角色与作品，覆盖标记仍保留。
            await tags.ReplaceCategoryTagsAsync(
                new[] { assetId }, AiTagCategory.Character, Array.Empty<string>());
            await tags.ReplaceCategoryTagsAsync(
                new[] { assetId }, AiTagCategory.Series, Array.Empty<string>());

            await aiTags.ReplaceAsync(assetId, ModelId, result);

            Assert.Empty(await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Character));
            Assert.Empty(await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Series));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Initialize_BackfillsExistingAiTagsIntoTagSystem()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var assetId = await AddAssetAsync(db, "hash-1", "a.jpg");
            await new AiTagRepository(db).ReplaceAsync(assetId, ModelId, CreateResult());

            // 模拟迁移 006 之前的库，结论只存在 asset_ai_tags 中，关联表还没有类别与模型列。
            await using (var connection = await db.OpenAsync())
            {
                await connection.ExecuteAsync("DELETE FROM asset_tags; DELETE FROM tags;");
                await connection.ExecuteAsync("ALTER TABLE asset_tags DROP COLUMN category;");
                await connection.ExecuteAsync("DROP INDEX IF EXISTS ix_asset_tags_model;");
                await connection.ExecuteAsync("ALTER TABLE asset_tags DROP COLUMN model_id;");
                await connection.ExecuteAsync("ALTER TABLE folders DROP COLUMN tag_model_id;");
                await connection.ExecuteAsync("DROP TABLE asset_category_overrides;");
                await connection.ExecuteAsync("PRAGMA user_version = 5;");
            }

            await db.InitializeAsync();

            var tags = new TagRepository(db);
            var assigned = await tags.GetTagsForAssetAsync(assetId);
            Assert.Equal(2, assigned.Count);

            var characters = await tags.GetTagsByCategoryAsync(assetId, AiTagCategory.Character);
            Assert.Single(characters);
            Assert.Equal("fujita kotone", characters[0].Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Stats_IgnoresAssetsWhoseFileIsMissing()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var kept = await AddAssetAsync(db, "hash-1", "a.jpg");
            var gone = await AddAssetAsync(db, "hash-2", "b.jpg");
            var repo = new AiTagRepository(db);
            await repo.ReplaceAsync(kept, ModelId, AiTagResult.Empty);

            // 磁盘上已经删掉的文件仍留在库里保留历史，但永远不可能再识别。
            // 统计分母若把它算进去，进度会永远到不了总数。
            await using (var connection = await db.OpenAsync())
            {
                var fileId = await connection.ExecuteScalarAsync<long>(
                    "SELECT id FROM asset_files WHERE asset_id = @AssetId AND is_primary = 1;",
                    new { AssetId = gone });
                await new AssetRepository(db).MarkMissingAsync(fileId);
            }

            Assert.Empty(await repo.GetPendingAsync(ModelId, 10));

            var stats = await repo.GetStatsAsync(ModelId);
            Assert.Equal(1, stats.Done);
            Assert.Equal(0, stats.Pending);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Pending_PagesByAssetId()
    {
        var (db, path) = await CreateDbAsync();
        try
        {
            var first = await AddAssetAsync(db, "hash-1", "a.jpg");
            var second = await AddAssetAsync(db, "hash-2", "b.jpg");
            await AddAssetAsync(db, "hash-3", "c.jpg");
            var repo = new AiTagRepository(db);

            var page = await repo.GetPendingAsync(ModelId, 2);
            Assert.Equal(new[] { first, second }, page.Select(job => job.AssetId).ToArray());

            page = await repo.GetPendingAsync(ModelId, 2, afterAssetId: second);
            Assert.Single(page);
            Assert.True(page[0].AssetId > second);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
