using ClipNest.App.Services;

namespace ClipNest.App.Tests;

public sealed class ThumbnailSizingTests
{
    [Fact]
    public void LongImage_CoversTileWidth()
    {
        // 200 宽的瓦片放 1:6 的长图，长边要取到 1200 才能让短边达到 200 像素。
        var size = ThumbnailSizing.ForBox(200, 1200, 1 / 6.0, 1, 2048);
        Assert.Equal(1280, size);
    }

    [Fact]
    public void WideImage_InSquareTile_CoversCenterCrop()
    {
        // 1.5:1 的横图铺满正方形瓦片时，宽度按高度放大到 1.5 倍，中心裁切后才不会模糊。
        var size = ThumbnailSizing.ForBox(340, 340, 1.5, 1, 2048);
        Assert.Equal(512, size);
    }

    [Fact]
    public void Scaling_RaisesBucket()
    {
        Assert.Equal(256, ThumbnailSizing.ForBox(200, 200, 1, 1, 2048));
        Assert.Equal(320, ThumbnailSizing.ForBox(200, 200, 1, 1.5, 2048));
    }

    [Fact]
    public void Max_CapsRequest()
    {
        Assert.Equal(1536, ThumbnailSizing.ForBox(200, 2000, 0.1, 2, 1536));
        Assert.Equal(1600, ThumbnailSizing.ForBox(200, 2000, 0.1, 2, 1600));
    }
}
