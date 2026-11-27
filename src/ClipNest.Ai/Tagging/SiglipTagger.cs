using System.Runtime.InteropServices;
using ClipNest.Ai.Acceleration;
using ClipNest.Core.Tagging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// SigLIP2 零样本标注器。图片只过一遍 224×224 视觉编码器，
/// 再与随程序分发的 4,585 条标签嵌入做余弦匹配。
/// </summary>
public sealed class SiglipTagger : ITagger, ITagPrefetcher
{
    private const string PixelInput = "pixel_values";
    private const string EmbeddingOutput = "pooler_output";
    private const string TagListFile = "ram_tag_list.txt";
    private const string EmbeddingFile = "tag_embeddings.npy";

    private readonly TagModelManager _models;
    private readonly IAiImageSource _images;
    private readonly string _assetRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();
    private readonly PixelPrefetch _prefetch = new();

    private InferenceSession? _session;
    private float[]? _embeddings;
    private string[]? _tags;
    private int _dimension;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private bool _disposed;

    /// <param name="assetRoot">标签表与预计算标签嵌入所在目录，随程序分发。</param>
    public SiglipTagger(TagModelManager models, IAiImageSource images, string assetRoot)
    {
        _models = models;
        _images = images;
        _assetRoot = assetRoot;
    }

    public SiglipTagger(TagModelManager models, string assetRoot)
        : this(models, SkiaAiImageSource.Instance, assetRoot)
    {
    }

    private string TagListPath => Path.Combine(_assetRoot, TagListFile);

    private string EmbeddingPath => Path.Combine(_assetRoot, EmbeddingFile);

    /// <summary>权重与随程序分发的标签数据都就绪时才可用。</summary>
    public bool IsAvailable =>
        _models.IsReady && File.Exists(TagListPath) && File.Exists(EmbeddingPath);

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
            return await Task.Run(() =>
            {
                var (session, embeddings, tags, dimension) = EnsureInitialized();
                _lastUsedUtc = DateTime.UtcNow;
                return Run(session, embeddings, tags, dimension, pixels);
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
            _tags = null;
            _dimension = 0;
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
        var size = _models.Definition.ImageSize;
        using var source = await _images.LoadAsync(path, size, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"无法解码图片：{path}");

        return await Task.Run(() => ToPixels(source, size), ct).ConfigureAwait(false);
    }

    private (InferenceSession Session, float[] Embeddings, string[] Tags, int Dimension)
        EnsureInitialized()
    {
        if (_session is not null && _embeddings is not null && _tags is not null)
        {
            return (_session, _embeddings, _tags, _dimension);
        }

        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_session is not null && _embeddings is not null && _tags is not null)
            {
                return (_session, _embeddings, _tags, _dimension);
            }

            if (!_models.IsReady)
            {
                throw new InvalidOperationException("SigLIP2 模型未安装，请先下载模型");
            }

            if (!File.Exists(TagListPath) || !File.Exists(EmbeddingPath))
            {
                throw new InvalidOperationException($"缺少标签数据：{_assetRoot}");
            }

            var tags = LoadTags(TagListPath);
            var (embeddings, rows, columns) = NpyFloatArray.Load(EmbeddingPath);
            if (rows != tags.Length)
            {
                throw new InvalidOperationException(
                    $"标签嵌入行数 {rows} 与标签数 {tags.Length} 不一致");
            }

            var session = OrtAccelerator.CreateSession(_models.ModelPath, allowGpu: true);

            _tags = tags;
            _embeddings = embeddings;
            _dimension = columns;
            _session = session;
            return (session, embeddings, tags, columns);
        }
    }

    private static string[] LoadTags(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].Trim();
        }

        return lines;
    }

    private static AiTagResult Run(
        InferenceSession session,
        float[] embeddings,
        string[] tags,
        int dimension,
        float[] pixels)
    {
        var size = (int)Math.Sqrt(pixels.Length / 3);
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(
                PixelInput, new DenseTensor<float>(pixels, new[] { 1, 3, size, size }))
        });

        var output = results.FirstOrDefault(result => result.Name == EmbeddingOutput)
            ?? results.Last();
        var image = output.AsEnumerable<float>().ToArray();
        Normalize(image);

        var cosines = new float[tags.Length];
        for (var tag = 0; tag < tags.Length; tag++)
        {
            cosines[tag] = Dot(embeddings, tag * dimension, image, dimension);
        }

        return SiglipSelection.Select(cosines, tags);
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 1e-12) return;

        var scale = (float)(1.0 / norm);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }
    }

    private static float Dot(float[] matrix, int start, float[] vector, int length)
    {
        var sum = 0f;
        for (var i = 0; i < length; i++)
        {
            sum += matrix[start + i] * vector[i];
        }

        return sum;
    }

    /// <summary>
    /// 缩放到 224×224 后转成 RGB 的 CHW 平面，归一化为 <c>(x/255-0.5)/0.5</c>。
    /// </summary>
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
            pixels[i] = rgba[i * 4] / 127.5f - 1f;
            pixels[pixelCount + i] = rgba[i * 4 + 1] / 127.5f - 1f;
            pixels[2 * pixelCount + i] = rgba[i * 4 + 2] / 127.5f - 1f;
        }

        return pixels;
    }
}
