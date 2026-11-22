using System.Runtime.InteropServices;
using System.Text.Json;
using ClipNest.Ai.Acceleration;
using ClipNest.Core.Tagging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// CAMIE 标注器，覆盖 70,527 个 danbooru 标签，输出角色、作品系列与画面特征。
/// 预处理按模型定义执行，等比缩放后居中补边，通道顺序为 RGB，布局为 NCHW；
/// v1 保持 0..1，v2 按 ImageNet 均值与标准差归一化。
/// </summary>
public sealed class CamieTagger : ITagger
{
    private readonly TagModelManager _models;
    private readonly IAiImageSource _images;
    private readonly int _imageSize;
    private readonly bool _normalizeImageNet;
    private readonly SKColor _padColor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();

    private string[]? _names;
    private string[]? _categories;
    private InferenceSession? _session;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private bool _disposed;

    public CamieTagger(TagModelManager models) : this(models, SkiaAiImageSource.Instance)
    {
    }

    public CamieTagger(TagModelManager models, IAiImageSource images)
    {
        _models = models;
        _images = images;

        var definition = models.Definition;
        _imageSize = definition.ImageSize;
        _normalizeImageNet = definition.NormalizeImageNet;
        _padColor = new SKColor(definition.PadRed, definition.PadGreen, definition.PadBlue);
    }

    public bool IsAvailable => _models.IsReady;

    public async Task<AiTagResult> TagAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var source = await _images.LoadAsync(path, _imageSize, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"无法解码图片：{path}");

            // 首次加载数百 MB 的模型需要数秒，放到后台线程避免卡住 UI。
            return await Task.Run(() =>
            {
                var (names, categories, session) = EnsureInitialized();
                var pixels = ToPixels(source);
                _lastUsedUtc = DateTime.UtcNow;
                return Run(session, names, categories, pixels);
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
            if (_session is null) return;
            if (DateTime.UtcNow - _lastUsedUtc < idleFor) return;

            _session.Dispose();
            _session = null;
            _names = null;
            _categories = null;
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
        _session?.Dispose();
        _gate.Dispose();
    }

    private (string[] Names, string[] Categories, InferenceSession Session) EnsureInitialized()
    {
        if (_names is not null && _categories is not null && _session is not null)
        {
            return (_names, _categories, _session);
        }

        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_names is not null && _categories is not null && _session is not null)
            {
                return (_names, _categories, _session);
            }

            if (!_models.IsReady)
            {
                throw new InvalidOperationException("标注模型未安装，请先下载模型");
            }

            var (names, categories) = LoadMetadata(_models.MetadataPath);

            // 固定 512x512、batch 1，适合放 GPU；DirectML 不可用时自动退回 CPU。
            var session = OrtAccelerator.CreateSession(_models.ModelPath, allowGpu: true);

            _names = names;
            _categories = categories;
            _session = session;
            return (names, categories, session);
        }
    }

    /// <summary>
    /// 元数据中的 idx_to_tag 为稀疏字典，这里展开成按索引访问的数组，供推理阶段直接读取。
    /// v1 的映射在根节点，v2 收在 dataset_info.tag_mapping 下，两处都按同一套键读取。
    /// </summary>
    private static (string[] Names, string[] Categories) LoadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var data = root.TryGetProperty("dataset_info", out var datasetInfo) ? datasetInfo : root;
        var mapping = data.TryGetProperty("tag_mapping", out var tagMapping) ? tagMapping : data;

        var total = data.TryGetProperty("total_tags", out var totalElement)
            ? totalElement.GetInt32()
            : mapping.GetProperty("idx_to_tag").GetPropertyCount();

        var names = new string[total];
        var categories = new string[total];

        if (mapping.TryGetProperty("idx_to_tag", out var idxToTag))
        {
            foreach (var entry in idxToTag.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out var index) || index < 0 || index >= total)
                {
                    continue;
                }

                names[index] = entry.Value.GetString() ?? string.Empty;
            }
        }

        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mapping.TryGetProperty("tag_to_category", out var tagToCategory))
        {
            foreach (var entry in tagToCategory.EnumerateObject())
            {
                var category = entry.Value.GetString();
                if (category is not null)
                {
                    lookup[entry.Name] = category;
                }
            }
        }

        for (var i = 0; i < total; i++)
        {
            var name = names[i];
            categories[i] = name.Length > 0 && lookup.TryGetValue(name, out var category)
                ? category
                : string.Empty;
        }

        return (names, categories);
    }

    private AiTagResult Run(
        InferenceSession session,
        string[] names,
        string[] categories,
        float[] pixels)
    {
        var inputName = session.InputMetadata.Keys.First();
        var input = NamedOnnxValue.CreateFromTensor(
            inputName, new DenseTensor<float>(pixels, new[] { 1, 3, _imageSize, _imageSize }));

        using var results = session.Run(new[] { input });

        // v1 输出名为 refined_output，v2 为 refined_predictions；没有命名时取第二个输出。
        var output = results.FirstOrDefault(r =>
                         r.Name.Contains("refined", StringComparison.OrdinalIgnoreCase))
            ?? (results.Count > 1 ? results[1] : results[0]);

        var logits = output.AsEnumerable<float>().ToArray();
        // 结论与分数快照在同一次推理中产出，避免为保存快照重复运行模型。
        return new AiTagResult(
            TagSelection.Select(logits, names, categories).Tags,
            TagSelection.SelectScores(logits, names, categories));
    }

    /// <summary>
    /// 把已解码的位图等比缩放到模型输入框内、居中补边，再转成 RGB 的 CHW 平面。
    /// </summary>
    private float[] ToPixels(SKBitmap source)
    {
        var scale = Math.Min(_imageSize / (float)source.Width, _imageSize / (float)source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var target = new SKBitmap(new SKImageInfo(
            _imageSize, _imageSize, SKColorType.Rgba8888, SKAlphaType.Unpremul));

        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(_padColor);
            var destination = new SKRect(
                (_imageSize - width) / 2f,
                (_imageSize - height) / 2f,
                (_imageSize + width) / 2f,
                (_imageSize + height) / 2f);
            // 默认采样为最近邻，缩小到模型输入尺寸会明显损失精度；改用高质量重采样以对齐训练分布。
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(source, destination, paint);
            canvas.Flush();
        }

        var pixelCount = _imageSize * _imageSize;
        var rgba = new byte[pixelCount * 4];
        Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);

        var pixels = new float[3 * pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            pixels[i] = Normalize(rgba[i * 4], 0.485f, 0.229f);
            pixels[pixelCount + i] = Normalize(rgba[i * 4 + 1], 0.456f, 0.224f);
            pixels[2 * pixelCount + i] = Normalize(rgba[i * 4 + 2], 0.406f, 0.225f);
        }

        return pixels;
    }

    private float Normalize(byte value, float mean, float std)
    {
        var scaled = value / 255f;
        return _normalizeImageNet ? (scaled - mean) / std : scaled;
    }
}
