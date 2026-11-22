using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ClipNest.App.Controls;

/// <summary>
/// SF Symbols 风格的描边图标。几何按 24×24 设计网格书写，渲染时等比缩放到控件尺寸；
/// 线宽按控件像素给定，因此 12px 与 20px 的图标视觉重量一致。
/// 描边色取 <see cref="Foreground"/>，该属性在 Avalonia 中可继承，按钮换色时图标自动跟随。
/// </summary>
public class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(Thickness), 1.5);

    public static readonly StyledProperty<Rect> ViewBoxProperty =
        AvaloniaProperty.Register<StrokeIcon, Rect>(nameof(ViewBox), new Rect(0, 0, 24, 24));

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, ThicknessProperty, ViewBoxProperty);
    }

    public StrokeIcon()
    {
        // 默认 Stretch 会让图标在 ContentPresenter 里贴左上角。ContentPresenter 按子元素自身的
        // 对齐属性摆放，不看按钮的 VerticalContentAlignment；图标必须显式居中。
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>以控件像素为单位的线宽；SF Symbols Regular 对应约 1.5。</summary>
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>几何的设计网格；默认 24×24。</summary>
    public Rect ViewBox
    {
        get => GetValue(ViewBoxProperty);
        set => SetValue(ViewBoxProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 基线 16×16，但不超过父容器给出的约束，避免在窄槽位里占满 16px。
        const double fallback = 16;
        var width = double.IsInfinity(availableSize.Width) ? fallback : Math.Min(fallback, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) ? fallback : Math.Min(fallback, availableSize.Height);
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var box = ViewBox;
        if (box.Width <= 0 || box.Height <= 0) return;

        var thickness = Math.Max(0.1, Thickness);
        // 内缩半个线宽，保证描边落在控件范围内而不是被裁切。
        var target = new Rect(Bounds.Size).Deflate(thickness / 2);
        if (target.Width <= 0 || target.Height <= 0) return;

        var scale = Math.Min(target.Width / box.Width, target.Height / box.Height);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return;

        var offsetX = target.X + (target.Width - box.Width * scale) / 2 - box.X * scale;
        var offsetY = target.Y + (target.Height - box.Height * scale) / 2 - box.Y * scale;

        var brush = Foreground ?? Brushes.White;
        var pen = new Pen(brush, thickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
