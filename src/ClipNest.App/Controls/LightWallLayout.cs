using Avalonia;

namespace ClipNest.App.Controls;

public static class LightWallLayout
{
    public static int GetColumnCount(double width, double tileSize, double gap)
        => Math.Max(1, (int)Math.Floor((width + gap) / (tileSize + gap)));

    public static int GetRowCount(int itemCount, int columns)
        => columns <= 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);

    public static Rect GetTileRect(
        int index, int columns, double tileSize, double gap, double scrollOffset)
    {
        var row = columns <= 0 ? 0 : index / columns;
        var col = columns <= 0 ? 0 : index % columns;
        return new Rect(
            col * (tileSize + gap),
            row * (tileSize + gap) - scrollOffset,
            tileSize,
            tileSize);
    }

    public static double GetInfluence(Point pointer, Point tileCenter, double sigma = 180)
    {
        var dx = pointer.X - tileCenter.X;
        var dy = pointer.Y - tileCenter.Y;
        return Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
    }

    /// <summary>越靠近视口边缘越暗，用于营造景深渐隐。</summary>
    public static double GetEdgeFade(Point tileCenter, Size viewport, double margin = 110)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) return 1;

        var edge = Math.Min(
            Math.Min(tileCenter.X, viewport.Width - tileCenter.X),
            Math.Min(tileCenter.Y, viewport.Height - tileCenter.Y));
        return 0.65 + 0.35 * Math.Clamp(edge / margin, 0, 1);
    }

    /// <summary>焦点处接近 1，远处降到约 0.7，保持整面墙仍然可读。</summary>
    public static double GetDepthOpacity(double influence, double edgeFade)
        => Math.Clamp((0.68 + 0.32 * influence) * edgeFade, 0, 1);

    /// <summary>焦点的瓦片略微放大，远处保持原尺寸。</summary>
    public static double GetDepthScale(double influence)
        => 0.995 + 0.035 * influence;

    /// <summary>
    /// 返回以目标宽高比为中心裁切的源区域。宽图裁左右，高图裁上下。
    /// </summary>
    public static Rect GetCenterCrop(Size imageSize, double targetAspect)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0 ||
            targetAspect <= 0 || double.IsNaN(targetAspect) || double.IsInfinity(targetAspect))
        {
            return new Rect(imageSize);
        }

        var imageAspect = imageSize.Width / imageSize.Height;
        if (imageAspect > targetAspect)
        {
            var width = imageSize.Height * targetAspect;
            return new Rect((imageSize.Width - width) / 2, 0, width, imageSize.Height);
        }

        if (imageAspect < targetAspect)
        {
            var height = imageSize.Width / targetAspect;
            return new Rect(0, (imageSize.Height - height) / 2, imageSize.Width, height);
        }

        return new Rect(imageSize);
    }
}
