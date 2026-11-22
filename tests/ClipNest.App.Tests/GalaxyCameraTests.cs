using Avalonia;
using ClipNest.App.Controls;

namespace ClipNest.App.Tests;

public sealed class GalaxyCameraTests
{
    [Fact]
    public void Camera_RoundTripsWorldAndScreen()
    {
        var camera = new GalaxyCamera { CenterX = 120, CenterY = -40, Zoom = 1.5 };
        var viewport = new Size(800, 600);

        var world = camera.ScreenToWorld(new Point(300, 200), viewport);

        var screen = camera.WorldToScreen(world, viewport);
        Assert.Equal(300, screen.X, 3);
        Assert.Equal(200, screen.Y, 3);
    }

    [Fact]
    public void Fit_CentersContent()
    {
        var camera = new GalaxyCamera();
        camera.Fit(new Rect(-500, -200, 1000, 400), new Size(800, 600), 80);

        Assert.Equal(0, camera.CenterX, 3);
        Assert.Equal(0, camera.CenterY, 3);
        Assert.True(camera.Zoom > 0.05 && camera.Zoom <= 4);
    }

    [Fact]
    public void ZoomAt_KeepsPointerWorldPointFixed()
    {
        var camera = new GalaxyCamera();
        var viewport = new Size(800, 600);
        var anchor = new Point(200, 150);
        var before = camera.ScreenToWorld(anchor, viewport);

        camera.ZoomAt(anchor, 1.25, viewport);

        var after = camera.ScreenToWorld(anchor, viewport);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
    }
}
