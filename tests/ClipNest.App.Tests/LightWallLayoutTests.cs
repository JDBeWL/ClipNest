using Avalonia;
using ClipNest.App.Controls;

namespace ClipNest.App.Tests;

public sealed class LightWallLayoutTests
{
    [Fact]
    public void Columns_And_Rows_Are_Calculated()
    {
        Assert.Equal(5, LightWallLayout.GetColumnCount(1000, 160, 12));
        Assert.Equal(2, LightWallLayout.GetRowCount(6, 5));
    }

    [Fact]
    public void TileRect_Offsets_By_Row_And_Scroll()
    {
        var rect = LightWallLayout.GetTileRect(5, 5, 160, 12, 100);
        Assert.Equal(0, rect.X);
        Assert.Equal(72, rect.Y);
        Assert.Equal(160, rect.Width);
    }

    [Fact]
    public void Influence_Peaks_At_Center()
    {
        Assert.Equal(1, LightWallLayout.GetInfluence(new Point(0, 0), new Point(0, 0)), 3);
        Assert.True(LightWallLayout.GetInfluence(new Point(1000, 1000), new Point(0, 0)) < 0.001);
    }

    [Fact]
    public void Depth_FadesEdgesAndBrightensFocus()
    {
        var viewport = new Size(800, 600);
        var center = LightWallLayout.GetEdgeFade(new Point(400, 300), viewport);
        var edge = LightWallLayout.GetEdgeFade(new Point(10, 300), viewport);

        Assert.Equal(1, center, 3);
        Assert.InRange(edge, 0.65, 0.7);
        Assert.True(LightWallLayout.GetDepthOpacity(1, center) >
                    LightWallLayout.GetDepthOpacity(0, edge));
        Assert.True(LightWallLayout.GetDepthScale(1) > LightWallLayout.GetDepthScale(0));
    }

    [Fact]
    public void CenterCrop_WideImage_CropsLeftAndRight()
    {
        var rect = LightWallLayout.GetCenterCrop(new Size(200, 100), 1);

        Assert.Equal(50, rect.X, 3);
        Assert.Equal(0, rect.Y, 3);
        Assert.Equal(100, rect.Width, 3);
        Assert.Equal(100, rect.Height, 3);
    }

    [Fact]
    public void CenterCrop_TallImage_CropsTopAndBottom()
    {
        var rect = LightWallLayout.GetCenterCrop(new Size(100, 200), 1);

        Assert.Equal(0, rect.X, 3);
        Assert.Equal(50, rect.Y, 3);
        Assert.Equal(100, rect.Width, 3);
        Assert.Equal(100, rect.Height, 3);
    }
}
