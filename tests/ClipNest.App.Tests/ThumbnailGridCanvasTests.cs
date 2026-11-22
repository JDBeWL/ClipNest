using Avalonia.Headless.XUnit;
using ClipNest.App.Controls;

namespace ClipNest.App.Tests;

public sealed class ThumbnailGridCanvasTests
{
    [AvaloniaFact]
    public void Grid_HasExpectedDefaults()
    {
        var grid = new ThumbnailGridCanvas();
        Assert.Equal(190, grid.MinTileWidth);
        Assert.Equal(14, grid.Gap);
        Assert.Equal(16, grid.Padding);
        Assert.Equal(0, grid.CornerRadius);
    }
}
