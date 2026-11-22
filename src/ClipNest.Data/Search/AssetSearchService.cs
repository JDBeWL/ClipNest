using ClipNest.Core.Models;
using ClipNest.Core.Search;
using ClipNest.Data.Repositories;

namespace ClipNest.Data.Search;

public interface IAssetSearchService
{
    Task<IReadOnlyList<AssetListItem>> SearchAsync(
        string queryText, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<AssetListItem>> GetRecentAsync(
        int limit = 500, CancellationToken ct = default);
}

public sealed class AssetSearchService : IAssetSearchService
{
    /// <summary>
    /// 混合搜索时带标签过滤的候选上限，避免语义重排前取回过多行。
    /// </summary>
    private const int CandidateLimit = 5000;

    private readonly TagRepository _tags;
    private readonly SearchRepository _search;
    private readonly SemanticSearchService? _semantic;

    public AssetSearchService(
        TagRepository tags, SearchRepository search, SemanticSearchService? semantic = null)
    {
        _tags = tags;
        _search = search;
        _semantic = semantic;
    }

    public async Task<IReadOnlyList<AssetListItem>> SearchAsync(
        string queryText, int limit = 200, CancellationToken ct = default)
    {
        var ast = new SearchQueryParser().Parse(queryText);

        if (TrySplitSemantic(ast, out var semanticNode, out var filterNode))
        {
            if (_semantic is null)
                throw new SearchParseException("语义模型未安装，请先下载模型");

            if (filterNode is MatchAllNode)
                return await _semantic.RankAllAsync(semanticNode.Text, limit, ct);

            var tagMap = await CollectTagIdsAsync(filterNode, ct);
            var compiled = SearchSqlCompiler.Compile(filterNode, tagMap);
            var candidates = await _search.QueryAsync(
                compiled, Math.Max(limit, CandidateLimit), ct);
            return await _semantic.RankAsync(semanticNode.Text, candidates, limit, ct);
        }

        var allTagMap = await CollectTagIdsAsync(ast, ct);
        var allCompiled = SearchSqlCompiler.Compile(ast, allTagMap);
        return await _search.QueryAsync(allCompiled, limit, ct);
    }

    public Task<IReadOnlyList<AssetListItem>> GetRecentAsync(
        int limit = 500, CancellationToken ct = default)
    {
        var compiled = SearchSqlCompiler.Compile(
            new MatchAllNode(), new Dictionary<string, IReadOnlyList<long>>());
        return _search.QueryAsync(compiled, limit, ct);
    }

    private async Task<Dictionary<string, IReadOnlyList<long>>> CollectTagIdsAsync(
        QueryNode node, CancellationToken ct)
    {
        var tagMap = new Dictionary<string, IReadOnlyList<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tagNode in CollectTags(node))
        {
            if (tagMap.ContainsKey(tagNode.Path)) continue;
            tagMap[tagNode.Path] = await _tags.GetTagIdsByPathAsync(
                tagNode.Path, tagNode.IncludeDescendants, ct);
        }

        return tagMap;
    }

    /// <summary>
    /// 拆分语义节点与过滤节点。支持纯 sem 节点与 And 中恰好一个语义节点。
    /// </summary>
    private static bool TrySplitSemantic(
        QueryNode ast, out SemanticNode semantic, out QueryNode filter)
    {
        semantic = null!;
        filter = ast;

        var count = CountSemantic(ast);
        if (count == 0) return false;
        if (count > 1) throw new SearchParseException("一次只支持一个语义条件");

        switch (ast)
        {
            case SemanticNode single:
                semantic = single;
                filter = new MatchAllNode();
                return true;
            case AndNode and:
            {
                var semanticChildren = and.Children.Where(child => child is SemanticNode).ToList();
                if (semanticChildren.Count != 1)
                    throw new SearchParseException("语义条件暂不支持与 | 或 - 组合");

                var rest = and.Children
                    .Where(child => !ReferenceEquals(child, semanticChildren[0]))
                    .ToList();
                if (rest.Any(ContainsSemantic))
                    throw new SearchParseException("语义条件暂不支持与 | 或 - 组合");

                semantic = (SemanticNode)semanticChildren[0];
                filter = rest.Count switch
                {
                    0 => new MatchAllNode(),
                    1 => rest[0],
                    _ => new AndNode(rest)
                };
                return true;
            }
            default:
                throw new SearchParseException("语义条件暂不支持与 | 或 - 组合");
        }
    }

    private static int CountSemantic(QueryNode node) => node switch
    {
        SemanticNode => 1,
        AndNode and => and.Children.Sum(CountSemantic),
        OrNode or => or.Children.Sum(CountSemantic),
        NotNode not => CountSemantic(not.Child),
        _ => 0
    };

    private static bool ContainsSemantic(QueryNode node) => CountSemantic(node) > 0;

    private static IEnumerable<TagNode> CollectTags(QueryNode node) => node switch
    {
        TagNode tag => new[] { tag },
        AndNode and => and.Children.SelectMany(CollectTags),
        OrNode or => or.Children.SelectMany(CollectTags),
        NotNode not => CollectTags(not.Child),
        _ => Enumerable.Empty<TagNode>()
    };
}
