using System.Runtime.InteropServices;
using System.Text;
using ClipNest.Ai.Acceleration;
using ClipNest.Core.Tagging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// SmilingWolf WD Tagger v3 标注器，覆盖 10,861 个 danbooru 特征与角色标签。
/// 透明像素合成到白底，等比缩放并居中白边补齐至 448×448；输入为 0..255 的 BGR，
/// 布局为 NHWC，模型输出已过 Sigmoid。
/// </summary>
public sealed class WdTagger : ITagger, ITagPrefetcher
{
    /// <summary>与官方默认特征阈值一致。</summary>
    public const float GeneralThreshold = 0.35f;

    /// <summary>角色候选下限；WD v3 词表仅含高频角色，低于此值多为外貌相近的误报。</summary>
    public const float CharacterFloor = 0.35f;

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

    public WdTagger(TagModelManager models) : this(models, SkiaAiImageSource.Instance)
    {
    }

    public WdTagger(TagModelManager models, IAiImageSource images)
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
                throw new InvalidOperationException("WD Tagger 模型未安装，请先下载模型");
            }

            var (names, categories) = LoadLabels(_models.MetadataPath);

            // 固定 448×448、batch 1，适合放 GPU；DirectML 不可用时自动退回 CPU。
            var session = OrtAccelerator.CreateSession(_models.ModelPath, allowGpu: true);

            _names = names;
            _categories = categories;
            _session = session;
            return (names, categories, session);
        }
    }

    /// <summary>
    /// 解析 <c>selected_tags.csv</c>，保留与模型输出对齐的行序。
    /// 类别沿用 danbooru 编号，0 为特征，1 为画师，3 为作品，4 为角色，9 为分级。
    /// </summary>
    internal static (string[] Names, string[] Categories) LoadLabels(string path)
    {
        var names = new List<string>(11_000);
        var categories = new List<string>(11_000);

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerSkipped = false;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!headerSkipped)
            {
                headerSkipped = true;
                if (line.StartsWith("tag_id,", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var fields = ParseCsvFields(line);
            if (fields.Count < 3)
            {
                continue;
            }

            var name = fields[1].Trim();
            var category = MapCategory(fields[2].Trim());
            names.Add(name);
            categories.Add(category);
        }

        return (names.ToArray(), categories.ToArray());
    }

    private static string MapCategory(string raw) => raw switch
    {
        "0" => AiTagCategory.General,
        "1" => AiTagCategory.Artist,
        "3" => AiTagCategory.Series,
        "4" => AiTagCategory.Character,
        _ => string.Empty
    };

    private static List<string> ParseCsvFields(string line)
    {
        var fields = new List<string>(4);
        var buffer = new StringBuilder(line.Length);
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        buffer.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    buffer.Append(ch);
                }
            }
            else if (ch == ',')
            {
                fields.Add(buffer.ToString());
                buffer.Clear();
            }
            else if (ch == '"' && buffer.Length == 0)
            {
                inQuotes = true;
            }
            else
            {
                buffer.Append(ch);
            }
        }

        fields.Add(buffer.ToString());
        return fields;
    }

    private AiTagResult Run(
        InferenceSession session,
        string[] names,
        string[] categories,
        float[] pixels)
    {
        var inputName = session.InputMetadata.Keys.First();
        var input = NamedOnnxValue.CreateFromTensor(
            inputName, new DenseTensor<float>(pixels, new[] { 1, _imageSize, _imageSize, 3 }));

        using var results = session.Run(new[] { input });
        var probabilities = results[0].AsEnumerable<float>().ToArray();

        // WD v3 图内已完成 Sigmoid，直接按概率筛选，避免二次压缩。
        return new AiTagResult(
            TagSelection.SelectFromProbabilities(
                probabilities, names, categories, GeneralThreshold, CharacterFloor).Tags,
            TagSelection.SelectScoresFromProbabilities(probabilities, names, categories));
    }

    /// <summary>
    /// 将位图等比缩放到正方形画布内，白底合成透明通道并居中补边，输出 0..255 的 BGR NHWC 张量。
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

        var pixels = new float[pixelCount * 3];
        for (var i = 0; i < pixelCount; i++)
        {
            var src = i * 4;
            var dst = i * 3;
            pixels[dst] = rgba[src + 2];
            pixels[dst + 1] = rgba[src + 1];
            pixels[dst + 2] = rgba[src];
        }

        return pixels;
    }
}
