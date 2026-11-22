using ClipNest.Core.Embedding;
using ClipNest.Core.Models;
using ClipNest.Core.Search;
using ClipNest.Data.Repositories;

namespace ClipNest.Data.Search;

/// <summary>
/// 语义排序，在候选集内按文本向量与图片向量的点积重排。
/// 向量已由编码器归一化，因此点积即余弦相似度。
/// </summary>
public sealed class SemanticSearchService
{
    private readonly EmbeddingRepository _embeddings;
    private readonly IClipEncoder _encoder;
    private readonly string _modelId;
    private readonly SearchRepository _search;

    public SemanticSearchService(
        EmbeddingRepository embeddings,
        SearchRepository search,
        IClipEncoder encoder,
        string modelId)
    {
        _embeddings = embeddings;
        _search = search;
        _encoder = encoder;
        _modelId = modelId;
    }

    /// <summary>
    /// 在给定候选集内按语义相似度降序重排，只保留向量表中存在的候选。
    /// </summary>
    public async Task<IReadOnlyList<AssetListItem>> RankAsync(
        string text, IReadOnlyList<AssetListItem> candidates, int limit, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (candidates.Count == 0 || limit <= 0) return Array.Empty<AssetListItem>();

        var query = await _encoder.EncodeTextAsync(text, ct);
        var vectors = await _embeddings.GetAllVectorsAsync(_modelId, ct);
        if (vectors.Count == 0) return Array.Empty<AssetListItem>();

        return candidates
            .Where(candidate => vectors.ContainsKey(candidate.Id))
            .Select(candidate => (Asset: candidate, Score: Dot(query, vectors[candidate.Id])))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Asset.Id)
            .Take(limit)
            .Select(item => item.Asset)
            .ToList();
    }

    /// <summary>
    /// 对全部已生成向量的资产排序，取前 limit 个再回表。
    /// </summary>
    public async Task<IReadOnlyList<AssetListItem>> RankAllAsync(
        string text, int limit, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (limit <= 0) return Array.Empty<AssetListItem>();

        var query = await _encoder.EncodeTextAsync(text, ct);
        var vectors = await _embeddings.GetAllVectorsAsync(_modelId, ct);
        if (vectors.Count == 0) return Array.Empty<AssetListItem>();

        var ids = vectors
            .Select(pair => (Id: pair.Key, Score: Dot(query, pair.Value)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Id)
            .Take(limit)
            .Select(item => item.Id)
            .ToList();

        // 回表并按分数顺序重排。
        return await _search.GetByIdsAsync(ids, ct);
    }

    private void EnsureAvailable()
    {
        if (!_encoder.IsAvailable)
            throw new SearchParseException("语义模型未安装，请先下载模型");
    }

    /// <summary>
    /// 点积；维度不一致时按较短者计算，避免越界。
    /// </summary>
    private static float Dot(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        var sum = 0f;
        for (var i = 0; i < length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }
}
