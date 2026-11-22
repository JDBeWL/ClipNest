using System.Globalization;
using ClipNest.Data.Repositories;

namespace ClipNest.Ai.Characters;

/// <summary>
/// 角色归属。用户的一次纠正被固化为可复用原型，供自动归类与候选建议使用。
/// </summary>
public sealed class CharacterService
{
    /// <summary>身份特征沿用语义向量。现有特征中，它对同一人物的区分度最高。</summary>
    public const string EmbeddingModelId = "chinese-clip-vit-base-patch16";

    /// <summary>
    /// 候选阈值相对原型凝聚度的下探幅度。同角色样本到原型约 0.92，
    /// 跨角色 0.79~0.86，因此 0.05 是仍能区分两者的最大下探幅度。
    /// </summary>
    private const double Tolerance = 0.05;

    /// <summary>
    /// 单次候选数量上限。原型样本少时凝聚度会漂移，阈值可能把整个图库都划进来；
    /// 限制数量后，即使判定过宽，也不会一次污染整个图库。
    /// </summary>
    private const int MaxCandidates = 24;

    private readonly CharacterRepository _repository;

    public CharacterService(CharacterRepository repository) => _repository = repository;

    public Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken ct = default)
        => _repository.GetNamesAsync(ct);

    /// <summary>取单张图的角色归属；用户确认优先于自动归类。</summary>
    public async Task<CharacterMatch?> GetMatchAsync(long assetId, CancellationToken ct = default)
    {
        var matches = await _repository.GetMatchesAsync(new[] { assetId }, ct);
        return matches.TryGetValue(assetId, out var match) ? match : null;
    }

    /// <summary>
    /// 把一批图片指定为某个角色。用户确认优先于模型标签与自动归类。
    /// 返回新角色 id。
    /// </summary>
    public async Task<long> AssignAsync(
        string name,
        IReadOnlyList<long> assetIds,
        CancellationToken ct = default)
    {
        var characterId = await _repository.EnsureAsync(name, ct);
        await _repository.AssignUserAsync(characterId, assetIds, ct);
        await RefreshPrototypeAsync(characterId, ct);
        return characterId;
    }

    /// <summary>清除这些图片的角色归属，并回收因此变空的角色。</summary>
    public async Task ClearAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        await _repository.RemoveAsync(assetIds, ct);
        await _repository.PruneEmptyAsync(ct);
    }

    /// <summary>按该角色现有样本重算特征中心。</summary>
    public async Task RefreshPrototypeAsync(long characterId, CancellationToken ct = default)
    {
        var vectors = await _repository.GetSampleVectorsAsync(characterId, EmbeddingModelId, ct);
        if (vectors.Count == 0)
        {
            return;
        }

        var centroid = Centroid(vectors);
        await _repository.SavePrototypeAsync(characterId, EmbeddingModelId, centroid, vectors.Count, ct);
    }

    /// <summary>
    /// 与原型足够接近、但还没归到该角色的图片，按相似度降序。阈值取样本自身到原型的
    /// 平均相似度再下探一定幅度，避免写死一个对所有角色都适用的绝对值。
    /// </summary>
    public async Task<IReadOnlyList<long>> SuggestAsync(long characterId, CancellationToken ct = default)
    {
        var prototypes = await _repository.GetPrototypesAsync(EmbeddingModelId, ct);
        var prototype = prototypes.FirstOrDefault(item => item.CharacterId == characterId);
        if (prototype is null)
        {
            return Array.Empty<long>();
        }

        var assigned = (await _repository.GetAssetIdsAsync(characterId, ct)).ToHashSet();
        var vectors = await _repository.GetAllVectorsAsync(EmbeddingModelId, ct);
        if (vectors.Count == 0)
        {
            return Array.Empty<long>();
        }

        // 原型凝聚度，即已确认样本与中心的平均相似度，用于衡量该角色样本的集中程度。
        var cohesion = assigned
            .Where(vectors.ContainsKey)
            .Select(id => Dot(vectors[id], prototype.Vector))
            .DefaultIfEmpty(1.0)
            .Average();
        var floor = cohesion - Tolerance;

        return vectors
            .Where(pair => !assigned.Contains(pair.Key))
            .Select(pair => (AssetId: pair.Key, Score: Dot(pair.Value, prototype.Vector)))
            .Where(item => item.Score >= floor)
            .OrderByDescending(item => item.Score)
            .Take(MaxCandidates)
            .Select(item => item.AssetId)
            .ToList();
    }

    /// <summary>把候选批量归入该角色，来源记为 auto，用户随时可覆盖。</summary>
    public async Task AssignSuggestedAsync(
        long characterId,
        IReadOnlyList<long> assetIds,
        CancellationToken ct = default)
    {
        var prototypes = await _repository.GetPrototypesAsync(EmbeddingModelId, ct);
        var prototype = prototypes.FirstOrDefault(item => item.CharacterId == characterId);
        var vectors = await _repository.GetAllVectorsAsync(EmbeddingModelId, ct);

        var scored = assetIds
            .Where(vectors.ContainsKey)
            .Select(id => (Id: id, Score: (float)(prototype is null ? 0 : Dot(vectors[id], prototype.Vector))))
            .ToList();

        await _repository.AssignAutoAsync(characterId, scored, ct);
        await RefreshPrototypeAsync(characterId, ct);
    }

    private static double Dot(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            sum += (double)left[i] * right[i];
        }

        return sum;
    }

    private static float[] Centroid(IReadOnlyList<float[]> vectors)
    {
        var dim = vectors[0].Length;
        var sum = new double[dim];
        foreach (var vector in vectors)
        {
            for (var i = 0; i < dim && i < vector.Length; i++)
            {
                sum[i] += vector[i];
            }
        }

        var centroid = new float[dim];
        double norm = 0;
        for (var i = 0; i < dim; i++)
        {
            centroid[i] = (float)(sum[i] / vectors.Count);
            norm += centroid[i] * (double)centroid[i];
        }

        norm = Math.Sqrt(norm);
        if (norm > 1e-9)
        {
            for (var i = 0; i < dim; i++)
            {
                centroid[i] = (float)(centroid[i] / norm);
            }
        }

        return centroid;
    }

    public static string FormatScore(double score)
        => score.ToString("0.00", CultureInfo.InvariantCulture);
}
