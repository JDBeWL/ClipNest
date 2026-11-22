using ClipNest.Core.Models;
using ClipNest.Core.Search;
using Dapper;

namespace ClipNest.Data.Search;

public sealed record CompiledSearch(string Sql, DynamicParameters Parameters);

public static class SearchSqlCompiler
{
    public static CompiledSearch Compile(
        QueryNode query,
        IReadOnlyDictionary<string, IReadOnlyList<long>> tagIdsByPath)
    {
        var parameters = new DynamicParameters();
        var counter = 0;
        var predicate = CompileNode(query, tagIdsByPath, parameters, ref counter);

        // 默认只展示磁盘上仍然存在的文件。外部删除后刷新，图库不应继续保留旧图。
        // 显式搜索 is:missing 时不过滤，否则该查询永远无结果。
        var visibility = ContainsMissingField(query)
            ? "1=1"
            : "EXISTS (SELECT 1 FROM asset_files afv WHERE afv.asset_id = a.id AND afv.file_state = 'indexed')";

        var sql = $"""
            SELECT a.id, a.sha256, a.file_name,
                   COALESCE(af.path, '') AS primary_path,
                   a.width, a.height, a.taken_at, a.created_at,
                   a.rating, a.is_favorite, a.decode_status
            FROM assets a
            LEFT JOIN asset_files af ON af.asset_id = a.id AND af.is_primary = 1
            WHERE {predicate}
              AND {visibility}
            ORDER BY COALESCE(a.taken_at, a.updated_at) DESC
            LIMIT @limit;
            """;
        return new CompiledSearch(sql, parameters);
    }

    private static bool ContainsMissingField(QueryNode node) => node switch
    {
        FieldNode field => string.Equals(field.Field, "is", StringComparison.OrdinalIgnoreCase)
            && string.Equals(field.Value, "missing", StringComparison.OrdinalIgnoreCase),
        AndNode and => and.Children.Any(ContainsMissingField),
        OrNode or => or.Children.Any(ContainsMissingField),
        NotNode not => ContainsMissingField(not.Child),
        _ => false
    };

    private static string CompileNode(
        QueryNode node,
        IReadOnlyDictionary<string, IReadOnlyList<long>> tagIdsByPath,
        DynamicParameters parameters,
        ref int counter)
    {
        switch (node)
        {
            case MatchAllNode:
                return "1=1";
            case AndNode and:
            {
                var parts = new List<string>();
                foreach (var child in and.Children)
                    parts.Add(CompileNode(child, tagIdsByPath, parameters, ref counter));
                return "(" + string.Join(" AND ", parts) + ")";
            }
            case OrNode or:
            {
                var parts = new List<string>();
                foreach (var child in or.Children)
                    parts.Add(CompileNode(child, tagIdsByPath, parameters, ref counter));
                return "(" + string.Join(" OR ", parts) + ")";
            }
            case NotNode not:
                return "NOT (" + CompileNode(not.Child, tagIdsByPath, parameters, ref counter) + ")";
            case TagNode tag:
                if (!tagIdsByPath.TryGetValue(tag.Path, out var ids) || ids.Count == 0) return "0=1";
                var tagParameter = $"tag{counter++}";
                parameters.Add(tagParameter, ids);
                return $"EXISTS (SELECT 1 FROM asset_tags at WHERE at.asset_id = a.id AND at.tag_id IN @{tagParameter})";
            case TextNode text:
                var ftsParameter = $"fts{counter++}";
                parameters.Add(ftsParameter, $"\"{text.Text.Replace("\"", "\"\"")}\"");
                var tagTextParameter = $"tagtext{counter++}";
                parameters.Add(tagTextParameter, TagPath.Normalize(text.Text));
                // 关键词同时匹配文件名与标签名；中文标签常用两字，trigram FTS 无法命中，
                // 因此标签走子串匹配。
                return $"(a.id IN (SELECT rowid FROM asset_fts WHERE asset_fts MATCH @{ftsParameter})"
                    + $" OR EXISTS (SELECT 1 FROM asset_tags ats JOIN tags ts ON ts.id = ats.tag_id"
                    + $" WHERE ats.asset_id = a.id AND instr(ts.normalized_name, @{tagTextParameter}) > 0))";
            case SemanticNode:
                throw new SearchParseException("语义搜索需要 CLIP 模型，当前版本未启用");
            case FieldNode field:
                return CompileField(field, parameters, ref counter);
            default:
                throw new SearchParseException("未知查询节点");
        }
    }

    private static string CompileField(FieldNode field, DynamicParameters parameters, ref int counter)
    {
        switch (field.Field)
        {
            case "year":
                if (field.Operator == "range")
                {
                    var parts = field.Value.Split("..", 2);
                    if (parts.Length != 2) throw new SearchParseException("年份范围格式应为 2020..2024");
                    var startParameter = $"y{counter++}";
                    var endParameter = $"y{counter++}";
                    parameters.Add(startParameter, $"{parts[0]}-01-01");
                    parameters.Add(endParameter, $"{parts[1]}-12-31");
                    return $"(a.taken_at >= @{startParameter} AND a.taken_at <= @{endParameter})";
                }
                var yearParameter = $"y{counter++}";
                parameters.Add(yearParameter, field.Value);
                return $"strftime('%Y', a.taken_at) = @{yearParameter}";
            case "camera":
                var cameraParameter = $"v{counter++}";
                parameters.Add(cameraParameter, $"%{field.Value}%");
                return $"(a.camera_make LIKE @{cameraParameter} OR a.camera_model LIKE @{cameraParameter})";
            case "lens":
                var lensParameter = $"v{counter++}";
                parameters.Add(lensParameter, $"%{field.Value}%");
                return $"a.lens LIKE @{lensParameter}";
            case "iso":
                return Compare("a.iso", field, parameters, ref counter);
            case "rating":
                return Compare("a.rating", field, parameters, ref counter);
            case "is":
                return field.Value.ToLowerInvariant() switch
                {
                    "favorite" => "a.is_favorite = 1",
                    "missing" => "EXISTS (SELECT 1 FROM asset_files af2 WHERE af2.asset_id = a.id AND af2.file_state = 'missing')",
                    "untagged" => "NOT EXISTS (SELECT 1 FROM asset_tags at2 WHERE at2.asset_id = a.id)",
                    _ => throw new SearchParseException($"未知 is 值: {field.Value}")
                };
            case "ext":
                var extParameter = $"v{counter++}";
                parameters.Add(extParameter, field.Value.StartsWith('.') ? field.Value : "." + field.Value);
                return $"EXISTS (SELECT 1 FROM asset_files af3 WHERE af3.asset_id = a.id AND af3.extension = @{extParameter} COLLATE NOCASE)";
            case "folder":
                var folderParameter = $"v{counter++}";
                parameters.Add(folderParameter, field.Value.TrimEnd('\\', '/') + "%");
                return $"EXISTS (SELECT 1 FROM asset_files af4 WHERE af4.asset_id = a.id AND af4.path LIKE @{folderParameter})";
            default:
                throw new SearchParseException($"未知字段: {field.Field}");
        }
    }

    private static string Compare(string column, FieldNode field, DynamicParameters parameters, ref int counter)
    {
        var sqlOperator = field.Operator switch
        {
            "eq" => "=",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            _ => throw new SearchParseException($"不支持的操作符: {field.Operator}")
        };
        var parameter = $"v{counter++}";
        parameters.Add(parameter, field.Value);
        return $"{column} {sqlOperator} @{parameter}";
    }
}
