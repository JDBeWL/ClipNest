using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using ClipNest.App.Theme;

namespace ClipNest.App.Controls;

/// <summary>
/// 紧凑的缩略图尺寸滑块。默认 Slider 的 Fluent 模板在窄高度下会裁掉大号圆形滑块，
/// 因此轨道自绘，滑块为 10px 圆点。
/// </summary>
public sealed class GalleryZoomSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<GalleryZoomSlider, double>(nameof(Minimum), 0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<GalleryZoomSlider, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GalleryZoomSlider, double>(
            nameof(Value), 0, defaultBindingMode: BindingMode.TwoWay);

    private const double SidePadding = 8;
    private const double TrackHeight = 4;

    private static Color TrackColor => ThemeColors.BorderSubtle;
    private static Color FillColor => ThemeColors.Accent;
    private static Color ThumbColor => ThemeColors.TextPrimary;
    private static Color ThumbStrokeColor => ThemeColors.Accent;

    private int _themeVersion = ThemeColors.Version;
    private IBrush _track = new SolidColorBrush(TrackColor);
    private IBrush _fill = new SolidColorBrush(FillColor);
    private IBrush _thumb = new SolidColorBrush(ThumbColor);
    private IPen _thumbPen = new Pen(new SolidColorBrush(ThumbStrokeColor), 1.4);
    private readonly Cursor _handCursor = new(StandardCursorType.Hand);

    private bool _dragging;
    private bool _hover;

    public GalleryZoomSlider()
    {
        Focusable = true;
        Cursor = _handCursor;
        MinWidth = 70;
        Height = 18;
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MinimumProperty ||
            change.Property == MaximumProperty ||
            change.Property == ValueProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    /// <summary>主题变化后重建缓存画刷。绘制前调用，开销只有一次版本比较。</summary>
    private void EnsureThemeBrushes()
    {
        if (_themeVersion == ThemeColors.Version) return;
        _themeVersion = ThemeColors.Version;
        _track = new SolidColorBrush(TrackColor);
        _fill = new SolidColorBrush(FillColor);
        _thumb = new SolidColorBrush(ThumbColor);
        _thumbPen = new Pen(new SolidColorBrush(ThumbStrokeColor), 1.4);
    }

    public override void Render(DrawingContext context)
    {
        EnsureThemeBrushes();
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= SidePadding * 2 || height <= 0) return;

        var left = SidePadding;
        var right = width - SidePadding;
        var centerY = height / 2;
        var fraction = GetFraction();
        var thumbX = left + (right - left) * fraction;

        context.DrawRectangle(
            _track, null,
            new Rect(left, centerY - TrackHeight / 2, right - left, TrackHeight), 2, 2);

        var filledWidth = Math.Max(0, thumbX - left);
        if (filledWidth > 0)
        {
            context.DrawRectangle(
                _fill, null,
                new Rect(left, centerY - TrackHeight / 2, filledWidth, TrackHeight), 2, 2);
        }

        var radius = _hover || _dragging ? 5.0 : 4.0;
        context.DrawEllipse(_thumb, _thumbPen, new Point(thumbX, centerY), radius, radius);
    }

    private double GetFraction()
    {
        var range = Maximum - Minimum;
        if (range <= 0) return 0;
        return Math.Clamp((Value - Minimum) / range, 0, 1);
    }

    private void SetFromX(double x)
    {
        var left = SidePadding;
        var right = Math.Max(left + 1, Bounds.Width - SidePadding);
        var fraction = Math.Clamp((x - left) / (right - left), 0, 1);
        Value = Minimum + fraction * (Maximum - Minimum);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _hover = true;
        InvalidateVisual();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = false;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Focus();
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromX(e.GetPosition(this).X);
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging) SetFromX(e.GetPosition(this).X);
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        InvalidateVisual();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Value = Math.Clamp(Value + Math.Sign(e.Delta.Y) * 8, Minimum, Maximum);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Left => -8,
            Key.Right => 8,
            Key.PageDown => -32,
            Key.PageUp => 32,
            _ => 0
        };

        if (e.Key == Key.Home) Value = Minimum;
        else if (e.Key == Key.End) Value = Maximum;
        else if (step != 0) Value = Math.Clamp(Value + step, Minimum, Maximum);
        else
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
    }
}
