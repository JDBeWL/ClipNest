using ClipNest.Imaging.Decoding;
using SkiaSharp;

namespace ClipNest.Imaging.Tests;

public sealed class WicImageDecoderTests
{
    [Fact]
    public async Task DecodesJpegThroughWic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-wic-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var bitmap = new SKBitmap(new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul)))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 90))
            using (var stream = File.Create(path))
            {
                data.SaveTo(stream);
            }

            var decoder = new WicImageDecoder();
            var info = await decoder.GetInfoAsync(path);
            Assert.NotNull(info);
            Assert.Equal(64, info.Width);
            Assert.Equal(32, info.Height);

            using var decoded = await decoder.DecodeAsync(path, maxSize: 32);
            Assert.NotNull(decoded);
            Assert.Equal(32, decoded.Width);
            Assert.Equal(16, decoded.Height);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodesHeicWhenSampleExists()
    {
        var path = Environment.GetEnvironmentVariable("CLIPNEST_HEIC_IMAGE")
            ?? @"E:\pic\神秘二次元图片\Other\HEIC测试\20250222_073330.heic";
        if (!File.Exists(path)) return;

        var decoder = new WicImageDecoder();
        var info = await decoder.GetInfoAsync(path);
        Assert.NotNull(info);

        using var decoded = await decoder.DecodeAsync(path, maxSize: 512);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width > 0 && decoded.Height > 0);
        Assert.True(Math.Max(decoded.Width, decoded.Height) <= 512);
    }
}
