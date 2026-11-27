using System.Runtime.InteropServices;
using System.Text.Json;
using ClipNest.Ai.Acceleration;
using ClipNest.Core.Tagging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// CL Tagger v1.02 标注器，覆盖 51,213 个 danbooru 标签，含角色、作品、画师与构图特征。
/// 透明像素合成到白底，等比缩放并居中白边补齐至 448×448；输入为 BGR，
/// 经 <c>(x/255-0.5)/0.5</c> 归一化为 NCHW，输出原始 logits。
/// </summary>
public sealed class ClTagger : ITagger, ITagPrefetcher
{
    /// <summary>特征阈值。CS-ASL 损失会拉高弱标签置信度，阈值高于 BCE 模型。</summary>
    public const float GeneralThreshold = 0.55f;

    /// <summary>角色候选下限，兼顾遮挡角色召回与 CS-ASL 分布。</summary>
    public const float CharacterFloor = 0.40f;

    /// <summary>无角色后缀时的独立作品阈值。</summary>
    public const float SeriesThreshold = 0.45f;

    /// <summary>角色带作品后缀时的系列匹配下限。</summary>
    public const float SeriesHintFloor = 0.15f;

    private readonly TagModelManager _models;
    private readonly IAiImageSource _images;
    private readonly int _imageSize;
    private readonly SKColor _padColor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();
    private readonly PixelPrefetch _prefetch = new();

    private string[]? _names;
    private string[]? _categories;
    private InferenceSession? _session;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private bool _disposed;

    public ClTagger(TagModelManager models) : this(models, SkiaAiImageSource.Instance)
    {
    }

    public ClTagger(TagModelManager models, IAiImageSource images)
    {
        _models = models;
        _images = images;

        var definition = models.Definition;
        _imageSize = definition.ImageSize;
        _padColor = new SKColor(definition.PadRed, definition.PadGreen, definition.PadBlue);
    }

    public bool IsAvailable => _models.IsReady;

    /// <inheritdoc/>
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
                var (names, categories, session) = EnsureInitialized();
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
            if (DateTime.UtcNow - _lastUsedUtc < idleFor) return;

            _prefetch.Clear();
            if (_session is null) return;

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
        _prefetch.Clear();
        _session?.Dispose();
        _gate.Dispose();
    }

    private async Task<float[]> LoadPixelsAsync(string path, CancellationToken ct)
    {
        using var source = await _images.LoadAsync(path, _imageSize, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"无法解码图片：{path}");

        return await Task.Run(() => ToPixels(source, _imageSize, _padColor), ct)
            .ConfigureAwait(false);
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
                throw new InvalidOperationException("CL Tagger 模型未安装，请先下载模型");
            }

            var (names, categories) = LoadMetadata(_models.MetadataPath);

            // 固定 448×448、batch 1，适合放 GPU；DirectML 不可用时自动退回 CPU。
            var session = OrtAccelerator.CreateSession(_models.ModelPath, allowGpu: true);

            _names = names;
            _categories = categories;
            _session = session;
            return (names, categories, session);
        }
    }

    /// <summary>
    /// 解析 <c>tag_mapping.json</c>，支持按数字索引键映射与 <c>idx_to_tag</c> 两种结构。
    /// 缺失下标填为空字符串，保证数组长度覆盖最大索引。
    /// </summary>
    internal static (string[] Names, string[] Categories) LoadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        if (root.TryGetProperty("idx_to_tag", out var idxToTag))
        {
            return LoadSplitSchema(root, idxToTag);
        }

        var entries = new List<(int Index, string Name, string Category)>();
        var maxIndex = -1;

        foreach (var entry in root.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var index) || index < 0)
            {
                continue;
            }

            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = entry.Value.TryGetProperty("tag", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            var rawCategory = entry.Value.TryGetProperty("category", out var categoryElement)
                ? categoryElement.GetString()
                : null;

            entries.Add((index, name, MapCategory(rawCategory)));
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        var total = maxIndex + 1;
        var names = CreateEmptyArray(total);
        var categories = CreateEmptyArray(total);
        foreach (var (index, name, category) in entries)
        {
            names[index] = name;
            categories[index] = category;
        }

        return (names, categories);
    }

    private static (string[] Names, string[] Categories) LoadSplitSchema(
        JsonElement root,
        JsonElement idxToTag)
    {
        var entries = new List<(int Index, string Name)>();
        var maxIndex = -1;

        foreach (var entry in idxToTag.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var index) || index < 0)
            {
                continue;
            }

            entries.Add((index, entry.Value.GetString() ?? string.Empty));
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("tag_to_category", out var tagToCategory))
        {
            foreach (var entry in tagToCategory.EnumerateObject())
            {
                var category = MapCategory(entry.Value.GetString());
                if (category.Length > 0)
                {
                    lookup[entry.Name] = category;
                }
            }
        }

        var total = maxIndex + 1;
        var names = CreateEmptyArray(total);
        var categories = CreateEmptyArray(total);
        foreach (var (index, name) in entries)
        {
            names[index] = name;
            if (name.Length > 0 && lookup.TryGetValue(name, out var category))
            {
                categories[index] = category;
            }
        }

        return (names, categories);
    }

    private static string[] CreateEmptyArray(int length)
    {
        if (length <= 0)
        {
            return Array.Empty<string>();
        }

        var array = new string[length];
        Array.Fill(array, string.Empty);
        return array;
    }

    private static string MapCategory(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (raw.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return AiTagCategory.General;
        }

        if (raw.Equals("Character", StringComparison.OrdinalIgnoreCase))
        {
            return AiTagCategory.Character;
        }

        if (raw.Equals("Copyright", StringComparison.OrdinalIgnoreCase))
        {
            return AiTagCategory.Series;
        }

        if (raw.Equals("Artist", StringComparison.OrdinalIgnoreCase))
        {
            return AiTagCategory.Artist;
        }

        return string.Empty;
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
        var logits = results[0].AsEnumerable<float>().ToArray();

        return new AiTagResult(
            TagSelection.Select(
                logits, names, categories,
                GeneralThreshold, CharacterFloor, SeriesThreshold, SeriesHintFloor).Tags,
            TagSelection.SelectScores(logits, names, categories));
    }

    /// <summary>
    /// 将位图等比缩放到正方形画布内，白底合成透明通道并居中补边，
    /// 输出按 <c>(x/255-0.5)/0.5</c> 归一化的 BGR CHW 平面。
    /// </summary>
    internal static float[] ToPixels(SKBitmap source, int imageSize, SKColor padColor)
    {
        var scale = Math.Min(imageSize / (float)source.Width, imageSize / (float)source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var left = (imageSize - width) / 2;
        var top = (imageSize - height) / 2;

        // 画布清为不透明底色，Premul 合成后每像素 Alpha 均为 255，RGB 通道无需逆预乘。
        using var target = new SKBitmap(new SKImageInfo(
            imageSize, imageSize, SKColorType.Rgba8888, SKAlphaType.Premul));

        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(padColor);
            var destination = new SKRect(left, top, left + width, top + height);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(source, destination, paint);
            canvas.Flush();
        }

        var pixelCount = imageSize * imageSize;
        var rgba = new byte[pixelCount * 4];
        Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);

        var pixels = new float[3 * pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var src = i * 4;
            pixels[i] = rgba[src + 2] / 127.5f - 1f;
            pixels[pixelCount + i] = rgba[src + 1] / 127.5f - 1f;
            pixels[2 * pixelCount + i] = rgba[src] / 127.5f - 1f;
        }

        return pixels;
    }
}
