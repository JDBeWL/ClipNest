using ClipNest.Imaging.Metadata;
using SkiaSharp;

namespace ClipNest.Imaging.Tests;

public sealed class ExifMetadataReaderTests
{
    [Fact]
    public async Task Read_NoExif_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.jpg");
        using (var bitmap = new SKBitmap(20, 10))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 90))
        await using (var stream = File.Create(path))
        {
            data.SaveTo(stream);
        }

        try
        {
            var metadata = await new ExifMetadataReader().ReadAsync(path);
            Assert.Null(metadata.TakenAt);
            Assert.Null(metadata.CameraModel);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Read_Png_ReturnsDimensionsFromDecoder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.png");
        using (var bitmap = new SKBitmap(30, 70))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        await using (var stream = File.Create(path))
        {
            data.SaveTo(stream);
        }

        try
        {
            var metadata = await new ExifMetadataReader().ReadAsync(path);
            Assert.Equal(30, metadata.Width);
            Assert.Equal(70, metadata.Height);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
