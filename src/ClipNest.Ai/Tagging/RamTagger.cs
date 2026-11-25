using System.Globalization;
using System.Runtime.InteropServices;
using ClipNest.Ai.Acceleration;
using ClipNest.Core.Tagging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// RAM++ 通用标注器，覆盖 4,585 个常见物体与场景标签。
/// 输入为 384×384 的 RGB 0..1，标签嵌入作为第二个输入，ONNX 图内完成 ImageNet 归一化。
/// </summary>
public sealed class RamTagger : ITagger, ITagPrefetcher
{
    private const string PixelInput = "pixel_values";
    private const string EmbeddingInput = "label_embed";
    private const string ScoreOutput = "tag_scores";
    private const float SnapshotFloor = 0.10f;

    private readonly TagModelManager _models;
    private readonly IAiImageSource _images;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();
    private readonly PixelPrefetch _prefetch = new();

    private InferenceSession? _session;
    private float[]? _embeddings;
    private string[]? _tags;
    private float[]? _thresholds;
    private int _embeddingRows;
    private int _embeddingColumns;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private bool _disposed;

    public RamTagger(TagModelManager models) : this(models, SkiaAiImageSource.Instance)
    {
    }

    public RamTagger(TagModelManager models, IAiImageSource images)
    {
        _models = models;
        _images = images;
    }

    public bool IsAvailable => _models.IsReady;

    /// <summary>预取下一张图片的输入。像素数据在解码后即驻留，与当前图片的推理并行。</summary>
    public void Prefetch(string path) =>
        _prefetch.Start(path, () => LoadPixelsAsync(path, CancellationToken.None));

    public async Task<AiTagResult> TagAsync(string path, CancellationToken ct = default)
    {
        // 解码与预处理不占用推理会话，可与上一张的推理重叠；命中预取时直接复用结果。
        var pixels = await (_prefetch.Take(path) ?? LoadPixelsAsync(path, ct))
            .ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 首次加载数百 MB 的标签嵌入与模型需要数秒，放到后台线程避免卡住 UI。
            return await Task.Run(() =>
            {
                var (session, embeddings, tags, thresholds, rows, columns) = EnsureInitialized();
                _lastUsedUtc = DateTime.UtcNow;
                return Run(session, embeddings, tags, thresholds, rows, columns, pixels);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseIfIdleAsync(TimeSpan idleFor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _lastUsedUtc < idleFor) return;

            _prefetch.Clear();
            if (_session is null) return;

            _session.Dispose();
            _session = null;
            _embeddings = null;
            _embeddingRows = 0;
            _embeddingColumns = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _prefetch.Clear();
        _session?.Dispose();
        _gate.Dispose();
    }

    private async Task<float[]> LoadPixelsAsync(string path, CancellationToken ct)
    {
        using var source = await _images.LoadAsync(path, _models.Definition.ImageSize, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"无法解码图片：{path}");

        return await Task.Run(() => ToPixels(source, _models.Definition.ImageSize), ct)
            .ConfigureAwait(false);
    }

    private (InferenceSession Session, float[] Embeddings, string[] Tags, float[] Thresholds,
        int Rows, int Columns) EnsureInitialized()
    {
        if (_session is not null && _embeddings is not null && _tags is not null
            && _thresholds is not null)
        {
            return (_session, _embeddings, _tags, _thresholds, _embeddingRows, _embeddingColumns);
        }

        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_session is not null && _embeddings is not null && _tags is not null
                && _thresholds is not null)
            {
                return (_session, _embeddings, _tags, _thresholds, _embeddingRows, _embeddingColumns);
            }

            if (!_models.IsReady)
            {
                throw new InvalidOperationException("RAM++ 模型未安装，请先下载模型");
            }

            var definition = _models.Definition;
            var tags = LoadLines(definition.FilePath(_models.Root, definition.Files[2].LocalPath));
            var thresholds = LoadThresholds(
                definition.FilePath(_models.Root, definition.Files[3].LocalPath), tags.Length);
            var (embeddings, rows, columns) = NpyFloatArray.Load(
                definition.FilePath(_models.Root, definition.Files[1].LocalPath));

            var session = OrtAccelerator.CreateSession(
                definition.FilePath(_models.Root, definition.Files[0].LocalPath), allowGpu: true);

            _tags = tags;
            _thresholds = thresholds;
            _embeddings = embeddings;
            _embeddingRows = rows;
            _embeddingColumns = columns;
            _session = session;
            return (session, embeddings, tags, thresholds, rows, columns);
        }
    }

    private static string[] LoadLines(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].Trim();
        }

        return lines;
    }

    private static float[] LoadThresholds(string path, int expectedCount)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length != expectedCount)
        {
            throw new InvalidOperationException(
                $"RAM++ 阈值数量 {lines.Length} 与标签数量 {expectedCount} 不一致");
        }

        var thresholds = new float[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            thresholds[i] = float.TryParse(
                lines[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0.5f;
        }

        return thresholds;
    }

    private static AiTagResult Run(
        InferenceSession session,
        float[] embeddings,
        string[] tags,
        float[] thresholds,
        int rows,
        int columns,
        float[] pixels)
    {
        var size = (int)Math.Sqrt(pixels.Length / 3);
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(
                PixelInput, new DenseTensor<float>(pixels, new[] { 1, 3, size, size })),
            NamedOnnxValue.CreateFromTensor(
                EmbeddingInput, new DenseTensor<float>(embeddings, new[] { rows, columns }))
        };

        using var results = session.Run(inputs);
        var output = results.FirstOrDefault(result => result.Name == ScoreOutput)
            ?? results.First();
        var raw = output.AsEnumerable<float>().ToArray();
        var scores = raw.Length == tags.Length ? raw : AggregateScores(raw, tags.Length);

        var selected = new List<AiTag>(TagSelection.MaxGeneralTags);
        var snapshot = new List<AiTagScore>(256);

        for (var i = 0; i < tags.Length; i++)
        {
            var name = tags[i];
            if (name.Length == 0)
            {
                continue;
            }

            var score = Math.Clamp(scores[i], 0f, 1f);
            if (score >= SnapshotFloor)
            {
                snapshot.Add(new AiTagScore(i, name, AiTagCategory.General, score));
            }

            if (score >= thresholds[i])
            {
                selected.Add(new AiTag(name, AiTagCategory.General, score));
            }
        }

        var top = selected
            .OrderByDescending(tag => tag.Score)
            .Take(TagSelection.MaxGeneralTags)
            .ToList();

        var snapshotRows = snapshot.Count <= TagSelection.MaxSnapshotTags
            ? snapshot.OrderBy(row => row.Index).ToList()
            : snapshot
                .OrderByDescending(row => row.Score)
                .Take(TagSelection.MaxSnapshotTags)
                .OrderBy(row => row.Index)
                .ToList();

        return new AiTagResult(top, snapshotRows);
    }

    /// <summary>
    /// RAM++ 为每个标签保存多组描述嵌入。输出维度大于标签数时，按标签分组取最大分。
    /// </summary>
    private static float[] AggregateScores(float[] raw, int tagCount)
    {
        if (raw.Length % tagCount != 0)
        {
            throw new InvalidOperationException(
                $"RAM++ 输出维度 {raw.Length} 与标签数 {tagCount} 不匹配");
        }

        var groupSize = raw.Length / tagCount;
        var scores = new float[tagCount];
        for (var tag = 0; tag < tagCount; tag++)
        {
            var start = tag * groupSize;
            var max = float.MinValue;
            for (var i = start; i < start + groupSize; i++)
            {
                if (raw[i] > max) max = raw[i];
            }

            scores[tag] = max;
        }

        return scores;
    }

    private static float[] ToPixels(SKBitmap source, int size)
    {
        using var target = new SKBitmap(new SKImageInfo(
            size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));

        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(source, new SKRect(0, 0, size, size), paint);
            canvas.Flush();
        }

        var pixelCount = size * size;
        var rgba = new byte[pixelCount * 4];
        Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);

        var pixels = new float[3 * pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            pixels[i] = rgba[i * 4] / 255f;
            pixels[pixelCount + i] = rgba[i * 4 + 1] / 255f;
            pixels[2 * pixelCount + i] = rgba[i * 4 + 2] / 255f;
        }

        return pixels;
    }
}
