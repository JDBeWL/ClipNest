using Avalonia;

namespace ClipNest.App.Controls;

/// <summary>
/// 星系画布相机，世界坐标以相机中心为原点，屏幕坐标以视口中心为原点。
/// </summary>
public sealed class GalaxyCamera
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 4.0;

    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Zoom { get; set; } = 1;

    public Point WorldToScreen(Point world, Size viewport) => new(
        (world.X - CenterX) * Zoom + viewport.Width / 2,
        (world.Y - CenterY) * Zoom + viewport.Height / 2);

    public Point ScreenToWorld(Point screen, Size viewport) => new(
        (screen.X - viewport.Width / 2) / Zoom + CenterX,
        (screen.Y - viewport.Height / 2) / Zoom + CenterY);

    public void ZoomAt(Point screen, double factor, Size viewport)
    {
        var world = ScreenToWorld(screen, viewport);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        CenterX = world.X - (screen.X - viewport.Width / 2) / Zoom;
        CenterY = world.Y - (screen.Y - viewport.Height / 2) / Zoom;
    }

    public void Fit(Rect worldBounds, Size viewport, double padding = 96)
    {
        CenterX = worldBounds.X + worldBounds.Width / 2;
        CenterY = worldBounds.Y + worldBounds.Height / 2;

        // 宽或高为 0 时只居中，保持当前缩放。
        if (worldBounds.Width <= 0 || worldBounds.Height <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(1, viewport.Width - 2 * padding);
        var availableHeight = Math.Max(1, viewport.Height - 2 * padding);
        var zoom = Math.Min(availableWidth / worldBounds.Width, availableHeight / worldBounds.Height);
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    public Matrix GetMatrix(Size viewport) =>
        Matrix.CreateTranslation(-CenterX, -CenterY)
        * Matrix.CreateScale(Zoom, Zoom)
        * Matrix.CreateTranslation(viewport.Width / 2, viewport.Height / 2);
}
