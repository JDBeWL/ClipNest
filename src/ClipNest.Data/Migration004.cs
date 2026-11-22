namespace ClipNest.Data;

/// <summary>
/// 迁移 004，标签分数快照。
/// 分数快照保存每张图在阈值之上的标签下标与分数，词表按模型单独存一份。
/// 调整阈值、按任意标签筛选与统计都只读数据库，不需要重新运行标注模型。
/// </summary>
public static class Migration004
{
    public const string Sql = """
        CREATE TABLE ai_tag_catalog (
          model_id TEXT NOT NULL,
          tag_index INTEGER NOT NULL,
          name TEXT NOT NULL,
          category TEXT NOT NULL,
          PRIMARY KEY(model_id, tag_index)
        );

        CREATE INDEX ix_ai_tag_catalog_name ON ai_tag_catalog(model_id, name);

        CREATE TABLE asset_ai_tag_scores (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          model_id TEXT NOT NULL,
          tag_index INTEGER NOT NULL,
          score REAL NOT NULL,
          PRIMARY KEY(asset_id, model_id, tag_index)
        );

        CREATE INDEX ix_ai_tag_scores_tag ON asset_ai_tag_scores(model_id, tag_index, score DESC);

        CREATE TABLE asset_ai_tag_runs (
          asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          model_id TEXT NOT NULL,
          tagged_at TEXT NOT NULL,
          PRIMARY KEY(asset_id, model_id)
        );

        CREATE INDEX ix_asset_ai_tag_runs_model ON asset_ai_tag_runs(model_id);
        """;
}
