namespace ClipNest.Data;

/// <summary>
/// 迁移 002，AI 识别结果单独成表，避免把上万个自动标签混进手写标签体系。
/// </summary>
public static class Migration002
{
    public const string Sql = """
        CREATE TABLE asset_ai_tags (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          tag TEXT NOT NULL,
          category TEXT NOT NULL,
          score REAL NOT NULL,
          model_id TEXT NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY(asset_id, tag, model_id)
        );

        CREATE INDEX ix_asset_ai_tags_category ON asset_ai_tags(model_id, category);
        """;
}
