namespace ClipNest.Data;

/// <summary>
/// 迁移 008，记录用户手动编辑过的角色与作品类别。
/// asset_tags 中的 manual 行无法表达用户清空角色，因为空列表没有行可留；
/// 单独记录覆盖标记后，AI 重新识别即可判断该类别已由用户接管。
/// </summary>
public static class Migration008
{
    public const string Sql = """
        CREATE TABLE asset_category_overrides (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          category TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          PRIMARY KEY(asset_id, category)
        );

        INSERT OR IGNORE INTO asset_category_overrides (asset_id, category, updated_at)
        SELECT asset_id, category, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        FROM asset_tags
        WHERE source = 'manual' AND category IN ('character', 'copyright')
        GROUP BY asset_id, category;
        """;
}
