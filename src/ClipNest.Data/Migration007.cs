namespace ClipNest.Data;

/// <summary>
/// 迁移 007，为标签关联记录类别。角色与作品需要单独展示并允许手动修改，类别存放在 asset_tags 上；
/// 手动编辑写入 source=manual 的同类别关联，AI 重识别不会覆盖；这里回填已有 AI 关联的类别。
/// </summary>
public static class Migration007
{
    public const string Sql = """
        ALTER TABLE asset_tags ADD COLUMN category TEXT;

        UPDATE asset_tags
        SET category = (
          SELECT a.category
          FROM asset_ai_tags a
          JOIN tags t ON t.id = asset_tags.tag_id
          WHERE a.asset_id = asset_tags.asset_id
            AND lower(trim(replace(a.tag, '_', ' '))) = t.normalized_name
          ORDER BY CASE a.category
            WHEN 'character' THEN 0
            WHEN 'copyright' THEN 1
            WHEN 'general' THEN 2
            ELSE 3
          END
          LIMIT 1
        )
        WHERE category IS NULL;
        """;
}
