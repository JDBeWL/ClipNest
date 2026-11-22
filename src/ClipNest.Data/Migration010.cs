namespace ClipNest.Data;

/// <summary>
/// 迁移 010，为文件夹扫描和默认排序补索引。
/// asset_files 只有 asset_id 索引，按文件夹读取会退化成全表扫描；
/// 默认排序使用 COALESCE(taken_at, updated_at)，普通列索引覆盖不到。
/// </summary>
public static class Migration010
{
    public const string Sql = """
        CREATE INDEX IF NOT EXISTS ix_asset_files_folder ON asset_files(folder_id);
        CREATE INDEX IF NOT EXISTS ix_assets_sort_at ON assets(COALESCE(taken_at, updated_at) DESC);
        """;
}