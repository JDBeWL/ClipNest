namespace ClipNest.Data;

/// <summary>
/// 迁移 005，为标注完成标记增加流水线版本。
/// 筛选规则升级后旧版本标记随之失效，队列按新规则重新识别；
/// 否则错误的系列标签会一直留存在库中。
/// </summary>
public static class Migration005
{
    public const string Sql = """
        ALTER TABLE asset_ai_tag_runs ADD COLUMN pipeline_version INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX ix_asset_ai_tag_runs_version ON asset_ai_tag_runs(model_id, pipeline_version);
        """;
}
