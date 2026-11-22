using ClipNest.Data;
using Dapper;

namespace ClipNest.Data.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public async Task Initialize_CreatesCoreTables_And_IsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.db");
        try
        {
            var db = new ClipNestDatabase(path);
            await db.InitializeAsync();
            await db.InitializeAsync();

            await using var connection = await db.OpenAsync();
            var tables = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='table'")).ToHashSet();

            Assert.Contains("assets", tables);
            Assert.Contains("asset_files", tables);
            Assert.Contains("tags", tables);
            Assert.Contains("asset_fts", tables);
            Assert.Contains("galaxy_nodes", tables);
            Assert.Contains("ai_tag_catalog", tables);
            Assert.Contains("asset_ai_tag_scores", tables);
            Assert.Contains("asset_ai_tag_runs", tables);

            var runColumns = (await connection.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('asset_ai_tag_runs');")).ToHashSet();
            Assert.Contains("pipeline_version", runColumns);

            var indexes = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='index'")).ToHashSet();
            Assert.Contains("ix_asset_files_folder", indexes);
            Assert.Contains("ix_assets_sort_at", indexes);
            Assert.Contains("ix_assets_galaxy_sort", indexes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
