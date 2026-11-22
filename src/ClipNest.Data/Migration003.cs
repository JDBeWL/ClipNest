namespace ClipNest.Data;

/// <summary>
/// 迁移 003，角色与原型。角色是用户命名的实体，原型是它的特征中心。
/// 自动归类结果与用户确认分开存放，用户确认始终优先。
/// </summary>
public static class Migration003
{
    public const string Sql = """
        CREATE TABLE characters (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          normalized_name TEXT NOT NULL UNIQUE,
          series TEXT,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );

        CREATE TABLE character_assets (
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          source TEXT NOT NULL,
          confidence REAL,
          created_at TEXT NOT NULL,
          PRIMARY KEY(character_id, asset_id)
        );

        CREATE INDEX ix_character_assets_asset ON character_assets(asset_id);

        CREATE TABLE character_prototypes (
          character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
          model_id TEXT NOT NULL,
          dim INTEGER NOT NULL,
          sample_count INTEGER NOT NULL,
          vector BLOB NOT NULL,
          updated_at TEXT NOT NULL
        );
        """;
}
