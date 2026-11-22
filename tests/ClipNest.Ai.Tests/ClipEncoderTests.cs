using ClipNest.Ai;
using ClipNest.Ai.Models;
using SkiaSharp;
using Xunit.Abstractions;

namespace ClipNest.Ai.Tests;

public sealed class ClipEncoderTests
{
    private readonly ITestOutputHelper _output;

    public ClipEncoderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Encoder_WithoutModel_IsUnavailableAndThrows()
    {
        var root = CreateTempDirectory();
        try
        {
            using var encoder = new ClipEncoder(new ClipModelManager(root));

            Assert.False(encoder.IsAvailable);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => encoder.EncodeTextAsync("海边的日落"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Encoder_WithModel_ProducesNormalizedEmbeddings()
    {
        var modelDir = Environment.GetEnvironmentVariable("CLIPNEST_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            return;
        }

        using var encoder = new ClipEncoder(new ClipModelManager(modelDir));
        Assert.True(encoder.IsAvailable);

        var root = CreateTempDirectory();
        try
        {
            var redPath = Path.Combine(root, "red.jpg");
            var bluePath = Path.Combine(root, "blue.jpg");
            SaveSolidJpeg(redPath, SKColors.Red);
            SaveSolidJpeg(bluePath, SKColors.Blue);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            var text = await encoder.EncodeTextAsync("海边的日落");
            var textElapsed = timer.ElapsedMilliseconds;

            timer.Restart();
            var red = await encoder.EncodeImageAsync(redPath);
            var imageElapsed = timer.ElapsedMilliseconds;

            timer.Restart();
            var blue = await encoder.EncodeImageAsync(bluePath);
            var secondImageElapsed = timer.ElapsedMilliseconds;
            _output.WriteLine(
                $"文本编码 {textElapsed}ms，图片编码 {imageElapsed}ms / {secondImageElapsed}ms");

            Assert.Equal(512, text.Length);
            Assert.Equal(512, red.Length);
            Assert.Equal(512, blue.Length);
            Assert.Equal(1.0, Norm(text), 3);
            Assert.Equal(1.0, Norm(red), 3);
            Assert.Equal(1.0, Norm(blue), 3);
            Assert.Contains(text, value => value != 0f);

            var cosine = Dot(red, blue);
            _output.WriteLine($"两张图片余弦相似度 {cosine:F4}");
            Assert.True(cosine < 0.999, $"两张纯色图片的余弦相似度应低于 0.999，实际为 {cosine}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void SaveSolidJpeg(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(224, 224, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    private static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        return Math.Sqrt(sum);
    }

    private static float Dot(float[] left, float[] right)
    {
        var sum = 0f;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-encoder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
