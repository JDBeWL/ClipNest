namespace ClipNest.Data;

/// <summary>迁移 011，为星系快照的时间倒序补表达式索引。</summary>
public static class Migration011
{
    public const string Sql = """
        CREATE INDEX IF NOT EXISTS ix_assets_galaxy_sort ON assets(COALESCE(taken_at, created_at) DESC, id DESC);
        """;
}