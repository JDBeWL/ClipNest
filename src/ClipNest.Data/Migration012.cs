namespace ClipNest.Data;

/// <summary>
/// 迁移 012，文件夹可以指定标注模型，AI 标签记录来源模型。
/// 文件夹的 tag_model_id 为空时跟随默认模型；
/// asset_tags 的 AI 行需要 model_id，切换模型后才能只清理该来源的标签。
/// </summary>
public static class Migration012
{
    public const string Sql = """
        ALTER TABLE folders ADD COLUMN tag_model_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE asset_tags ADD COLUMN model_id TEXT;
        UPDATE asset_tags SET model_id = 'camie-tagger' WHERE source = 'ai';
        CREATE INDEX IF NOT EXISTS ix_asset_tags_model ON asset_tags(model_id) WHERE model_id IS NOT NULL;
        """;
}
