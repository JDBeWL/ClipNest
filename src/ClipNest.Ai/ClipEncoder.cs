using System.Runtime.InteropServices;
using ClipNest.Ai.Acceleration;
using ClipNest.Ai.Models;
using ClipNest.Core.Embedding;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace ClipNest.Ai;

/// <summary>
/// 基于 ONNX Runtime 的 Chinese-CLIP 编码器，输出 L2 归一化向量。
/// </summary>
public sealed class ClipEncoder : IClipEncoder, IDisposable
{
    private const int ImageSize = 224;
    private const int PixelCount = ImageSize * ImageSize;

    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    private readonly ClipModelManager _models;
    private readonly IAiImageSource _images;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly object _initLock = new();

    private ClipTokenizer? _tokenizer;
    private InferenceSession? _session;
    private bool _disposed;

    public ClipEncoder(ClipModelManager models) : this(models, SkiaAiImageSource.Instance)
    {
    }

    public ClipEncoder(ClipModelManager models, IAiImageSource images)
    {
        _models = models;
        _images = images;
    }

    public bool IsAvailable => _models.IsReady;

    public async Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 首次加载 191MB 模型可能耗时 1s 以上，放到后台线程避免阻塞 UI。
            return await Task.Run(() =>
            {
                var (tokenizer, session) = EnsureInitialized();
                var tokens = tokenizer.Encode(text);
                var pixels = new float[3 * PixelCount];
                return Run(session, tokens, pixels, "text_embeds");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<float[]> EncodeImageAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var source = await _images.LoadAsync(path, ImageSize, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"无法解码图片：{path}");

            return await Task.Run(() =>
            {
                var (tokenizer, session) = EnsureInitialized();
                // 图片分支仍需要文本输入，使用空的 [CLS]/[SEP]。
                var tokens = tokenizer.Encode(string.Empty);
                var pixels = ToPixels(source);
                return Run(session, tokens, pixels, "image_embeds");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
            }
            finally
            {
                _gate.Release();
            }
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

    /// <summary>
    /// 懒加载 tokenizer 与推理会话，模型下载完成后无需重启。
    /// </summary>
    private (ClipTokenizer Tokenizer, InferenceSession Session) EnsureInitialized()
    {
        if (_tokenizer is not null && _session is not null)
        {
            return (_tokenizer, _session);
        }

        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_tokenizer is not null && _session is not null)
            {
                return (_tokenizer, _session);
            }

            if (!_models.IsReady)
            {
                throw new InvalidOperationException("语义模型未安装，请先下载模型");
            }

            var tokenizer = new ClipTokenizer(_models.VocabPath);

            // 文本长度是动态的，DirectML 会为每种形状重新编译计算图；
            // 单张 224x224 编码在 CPU 上只有 74ms，放 GPU 不划算。
            var session = OrtAccelerator.CreateSession(_models.ModelPath, allowGpu: false);

            _tokenizer = tokenizer;
            _session = session;
            return (tokenizer, session);
        }
    }

    private static float[] Run(InferenceSession session, ClipTokens tokens, float[] pixels, string outputName)
    {
        var length = tokens.InputIds.Length;
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(
                "input_ids", new DenseTensor<long>(tokens.InputIds, new[] { 1, length })),
            NamedOnnxValue.CreateFromTensor(
                "attention_mask", new DenseTensor<long>(tokens.AttentionMask, new[] { 1, length })),
            NamedOnnxValue.CreateFromTensor(
                "pixel_values", new DenseTensor<float>(pixels, new[] { 1, 3, ImageSize, ImageSize }))
        };

        using var results = session.Run(inputs);
        var output = results.FirstOrDefault(r => r.Name == outputName)
            ?? throw new InvalidOperationException($"模型输出缺少 {outputName}");
        return Normalize(output.AsEnumerable<float>().ToArray());
    }

    /// <summary>
    /// 把已解码的位图拉伸到 224×224，转成 CHW 归一化像素。
    /// </summary>
    private static float[] ToPixels(SKBitmap source)
    {
        using var target = new SKBitmap(new SKImageInfo(
            ImageSize, ImageSize, SKColorType.Rgba8888, SKAlphaType.Unpremul));

        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(source, new SKRect(0, 0, ImageSize, ImageSize));
            canvas.Flush();
        }

        var rgba = new byte[PixelCount * 4];
        Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);

        var pixels = new float[3 * PixelCount];
        for (var i = 0; i < PixelCount; i++)
        {
            var r = rgba[i * 4] / 255f;
            var g = rgba[i * 4 + 1] / 255f;
            var b = rgba[i * 4 + 2] / 255f;

            pixels[i] = (r - Mean[0]) / Std[0];
            pixels[PixelCount + i] = (g - Mean[1]) / Std[1];
            pixels[2 * PixelCount + i] = (b - Mean[2]) / Std[2];
        }

        return pixels;
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        var norm = Math.Sqrt(sum);
        if (norm > 1e-12)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }
}
