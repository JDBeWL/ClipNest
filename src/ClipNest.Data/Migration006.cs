namespace ClipNest.Data;

/// <summary>
/// 迁移 006，把已有的识别结果回填成普通标签。
/// 角色、作品与画面特征写入 tags 与 asset_tags，与手写标签一起展示和增删。
/// 这里只回填当前流水线版本的结论。
/// </summary>
public static class Migration006
{
    public const string Sql = """
        INSERT OR IGNORE INTO tags (name, normalized_name, parent_id, color, created_at)
        SELECT MIN(display_name), normalized_name, NULL, NULL,
               strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        FROM (
          SELECT replace(t.tag, '_', ' ') AS display_name,
                 lower(trim(replace(t.tag, '_', ' '))) AS normalized_name
          FROM asset_ai_tags t
          JOIN asset_ai_tag_runs r
            ON r.asset_id = t.asset_id AND r.model_id = t.model_id
          WHERE r.pipeline_version = 2
        )
        GROUP BY normalized_name;

        INSERT OR IGNORE INTO asset_tags (asset_id, tag_id, source, confidence)
        SELECT t.asset_id, tags.id, 'ai', t.score
        FROM asset_ai_tags t
        JOIN asset_ai_tag_runs r
          ON r.asset_id = t.asset_id AND r.model_id = t.model_id
        JOIN tags
          ON COALESCE(tags.parent_id, 0) = 0
         AND tags.normalized_name = lower(trim(replace(t.tag, '_', ' ')))
        WHERE r.pipeline_version = 2;
        """;
}
