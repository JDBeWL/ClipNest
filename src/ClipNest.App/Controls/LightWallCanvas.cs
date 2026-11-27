using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClipNest.App.Services;
using ClipNest.App.Theme;
using ClipNest.Core.Models;

namespace ClipNest.App.Controls;

public sealed class LightWallCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<AssetListItem>?> ItemsProperty =
        AvaloniaProperty.Register<LightWallCanvas, IReadOnlyList<AssetListItem>?>(nameof(Items));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<LightWallCanvas, double>(nameof(CornerRadius));

    // 光墙展示图库全部条目，纹理缓存按上限淘汰，避免位图持续驻留内存。
    private const long MaxTextureBytes = 128L * 1024 * 1024;
    private const long MaxBlurredBytes = 16L * 1024 * 1024;

    private readonly Dictionary<string, Bitmap> _textures = new();
    private readonly HashSet<string> _loading = new();
    private readonly HashSet<string> _failed = new();
    private readonly Dictionary<string, Bitmap> _blurred = new();
    private readonly HashSet<string> _loadingBlur = new();
    private readonly HashSet<string> _blurFailed = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new();
    private readonly LinkedList<string> _blurLru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _blurLruIndex = new();
    private long _textureBytes;
    private long _blurredBytes;
    private readonly SemaphoreSlim _blurGate = new(4);
    private int _themeVersion = ThemeColors.Version;
    private IBrush _tileBrush = new SolidColorBrush(ThemeColors.SurfaceCard);
    private IBrush _shadowBrush = new SolidColorBrush(ThemeColors.Shadow);
    private IBrush _background = CreateBackground();
    private IPen _focusPen = new Pen(new SolidColorBrush(ThemeColors.Accent), 1);
    private IPen _selectionPen = new Pen(new SolidColorBrush(ThemeColors.Accent), 2);
    private bool _frameScheduled;
    private double _lastFrameMs = -1;
    private double _scroll;
    private double _targetScroll;
    private Point _pointer = new(-10000, -10000);
    private Point _pointerRender = new(-10000, -10000);
    private bool _dragging;
    private Point _dragStart;
    private double _dragScroll;
    private Point _pressPoint;
    private AssetListItem? _pressedItem;
    private long? _selectedItemId;
    private INotifyCollectionChanged? _observedItems;

    public IReadOnlyList<AssetListItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public IThumbnailProvider? ThumbnailProvider { get; set; }
    public double TileSize { get; set; } = 160;
    public double Gap { get; set; } = 12;
    public event EventHandler<AssetListItem>? TileActivated;
    public event EventHandler<AssetListItem>? TileSelected;

    public LightWallCanvas()
    {
        ClipToBounds = true;
    }

    public void SelectItem(long? id)
    {
        if (_selectedItemId == id) return;
        _selectedItemId = id;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ItemsProperty)
        {
            if (_observedItems is not null)
                _observedItems.CollectionChanged -= OnItemsCollectionChanged;
            _observedItems = Items as INotifyCollectionChanged;
            if (_observedItems is not null)
                _observedItems.CollectionChanged += OnItemsCollectionChanged;
            InvalidateVisual();
        }

        if (change.Property == CornerRadiusProperty) InvalidateVisual();

        // 光墙的缓动按合成器帧推进，使高刷屏上的帧步进与刷新节奏保持一致。
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            RequestFrame();

        base.OnPropertyChanged(change);
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _textures.Clear();
            _loading.Clear();
            _failed.Clear();
            _blurred.Clear();
            _loadingBlur.Clear();
            _blurFailed.Clear();
            _lru.Clear();
            _lruIndex.Clear();
            _blurLru.Clear();
            _blurLruIndex.Clear();
            _textureBytes = 0;
            _blurredBytes = 0;
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
            _targetScroll = _scroll;
        }
        InvalidateVisual();
    }

    /// <summary>主题变化后重建缓存画刷。绘制前调用，开销只有一次版本比较。</summary>
    private void EnsureThemeBrushes()
    {
        if (_themeVersion == ThemeColors.Version) return;
        _themeVersion = ThemeColors.Version;
        _tileBrush = new SolidColorBrush(ThemeColors.SurfaceCard);
        _shadowBrush = new SolidColorBrush(ThemeColors.Shadow);
        _background = CreateBackground();
        _focusPen = new Pen(new SolidColorBrush(ThemeColors.Accent), 1);
        _selectionPen = new Pen(new SolidColorBrush(ThemeColors.Accent), 2);
    }

    public override void Render(DrawingContext context)
    {
        EnsureThemeBrushes();
        using var clip = context.PushClip(
            new RoundedRect(new Rect(Bounds.Size), this.CornerRadius));
        context.FillRectangle(_background, new Rect(Bounds.Size));
        var items = Items;
        if (items is null || items.Count == 0) return;

        var columns = LightWallLayout.GetColumnCount(Bounds.Width, TileSize, Gap);
        var rowHeight = TileSize + Gap;
        var firstRow = Math.Max(0, (int)Math.Floor(_scroll / rowHeight));
        var lastRow = Math.Min(
            (items.Count - 1) / columns,
            (int)Math.Ceiling((_scroll + Bounds.Height) / rowHeight));

        var hasPointer = _pointerRender.X > -1000;
        var pointer = _pointerRender;

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                if (index >= items.Count) break;

                var item = items[index];
                var rect = LightWallLayout.GetTileRect(index, columns, TileSize, Gap, _scroll);
                if (!rect.Intersects(new Rect(Bounds.Size))) continue;

                var center = rect.Center;
                var influence = hasPointer
                    ? LightWallLayout.GetInfluence(pointer, center)
                    : 0;
                var selected = _selectedItemId == item.Id;
                if (selected) influence = Math.Max(influence, 0.85);

                var edgeFade = LightWallLayout.GetEdgeFade(center, Bounds.Size);
                var opacity = LightWallLayout.GetDepthOpacity(
                    hasPointer ? influence : 1, edgeFade);
                var scale = hasPointer ? LightWallLayout.GetDepthScale(influence) : 1.0;
                var dx = center.X - pointer.X;
                var dy = center.Y - pointer.Y;
                var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var offset = hasPointer ? influence * 6 : 0;
                var drawRect = new Rect(
                    rect.X + dx / length * offset,
                    rect.Y + dy / length * offset,
                    rect.Width,
                    rect.Height);

                using (context.PushOpacity(opacity))
                using (context.PushTransform(
                    Matrix.CreateTranslation(drawRect.Center.X, drawRect.Center.Y) *
                    Matrix.CreateScale(scale, scale) *
                    Matrix.CreateTranslation(-drawRect.Center.X, -drawRect.Center.Y)))
                {
                    if (influence > 0.2)
                    {
                        using (context.PushOpacity(Math.Min(1, (influence - 0.2) / 0.45) * 0.4))
                        {
                            context.DrawRectangle(
                                _shadowBrush,
                                null,
                                drawRect.Translate(new Vector(0, 4 + influence * 4)).Inflate(1),
                                10,
                                10);
                        }
                    }

                    context.DrawRectangle(_tileBrush, null, drawRect, 8, 8);
                    DrawTileImage(context, item, drawRect, influence, hasPointer);

                    if (influence > 0.7)
                    {
                        using (context.PushOpacity((influence - 0.7) / 0.3 * 0.5))
                        {
                            context.DrawRectangle(
                                null, _focusPen, drawRect.Inflate(2), 10, 10);
                        }
                    }

                    if (selected)
                    {
                        context.DrawRectangle(
                            null, _selectionPen, drawRect.Deflate(1), 7, 7);
                    }
                }

                _ = LoadAsync(item);
                if (!hasPointer || influence < 0.6) RequestBlurred(item);
            }
        }
    }

    private void DrawTileImage(
        DrawingContext context, AssetListItem item, Rect rect, double influence, bool hasPointer)
    {
        var hasSharp = _textures.TryGetValue(item.Sha256, out var sharp);
        var hasSoft = _blurred.TryGetValue(item.Sha256, out var soft);
        if (hasSharp) TouchTexture(item.Sha256);
        if (hasSoft) TouchBlurred(item.Sha256);

        if (!hasPointer || influence >= 0.62)
        {
            if (hasSharp) DrawBitmap(context, sharp!, rect, 1);
            else if (hasSoft) DrawBitmap(context, soft!, rect, 1);
            return;
        }

        // 远景先用预模糊小图，再按焦点距离淡入清晰图。
        if (hasSoft) DrawBitmap(context, soft!, rect, 1);
        if (hasSharp && influence > 0.08)
        {
            var sharpOpacity = Math.Clamp((influence - 0.08) / 0.54, 0, 1);
            DrawBitmap(context, sharp!, rect, sharpOpacity);
        }
    }

    private static void DrawBitmap(
        DrawingContext context, Bitmap bitmap, Rect rect, double opacity)
    {
        var source = LightWallLayout.GetCenterCrop(
            new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height),
            rect.Width / rect.Height);

        // 模糊图本身已经是低分辨率，放大时不需要高质量重采样。
        using (context.PushOpacity(opacity))
        using (context.PushClip(new RoundedRect(rect, 8)))
        {
            context.DrawImage(bitmap, source, rect);
        }
    }

    private static IBrush CreateBackground()
    {
        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ThemeColors.SurfaceNavigation, 0),
                new GradientStop(ThemeColors.SurfaceWindow, 1)
            }
        };
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        if (_pointer.X > -1000) RequestFrame();

        if (_dragging)
        {
            var delta = _pointer.Y - _dragStart.Y;
            _scroll = Math.Clamp(_dragScroll - delta, 0, MaxScroll);
            _targetScroll = _scroll;
        }
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        _pointer = point;
        _pointerRender = point;

        if (e.ClickCount == 2)
        {
            var item = HitTest(point);
            if (item is not null) TileActivated?.Invoke(this, item);
            e.Handled = true;
            return;
        }

        _pressedItem = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? HitTest(point)
            : null;
        _pressPoint = point;
        _dragging = true;
        _dragStart = point;
        _dragScroll = _scroll;
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);

        var point = e.GetPosition(this);
        var moved = Math.Abs(point.X - _pressPoint.X) > 4 || Math.Abs(point.Y - _pressPoint.Y) > 4;
        if (_pressedItem is not null && !moved)
            TileSelected?.Invoke(this, _pressedItem);
        _pressedItem = null;

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _targetScroll = Math.Clamp(_targetScroll - e.Delta.Y * 80, 0, MaxScroll);
        RequestFrame();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointer = new Point(-10000, -10000);
        _pointerRender = _pointer;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    private double MaxScroll => Items is null
        ? 0
        : Math.Max(
            0,
            LightWallLayout.GetRowCount(
                Items.Count,
                LightWallLayout.GetColumnCount(Bounds.Width, TileSize, Gap)) *
            (TileSize + Gap) - Bounds.Height);

    private AssetListItem? HitTest(Point point)
    {
        var items = Items;
        if (items is null) return null;

        var columns = LightWallLayout.GetColumnCount(Bounds.Width, TileSize, Gap);
        for (var i = 0; i < items.Count; i++)
        {
            var rect = LightWallLayout.GetTileRect(i, columns, TileSize, Gap, _scroll);
            if (rect.Contains(point)) return items[i];
        }
        return null;
    }

    /// <summary>
    /// 预约合成器的下一帧。滚动和指针平滑都用帧回调推进，
    /// 避免 16ms 定时器在高刷屏上与刷新节奏错位。
    /// </summary>
    private void RequestFrame()
    {
        if (_frameScheduled || !IsVisible) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        _frameScheduled = true;
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        _frameScheduled = false;
        if (!IsVisible)
        {
            _lastFrameMs = -1;
            return;
        }

        var now = time.TotalMilliseconds;
        var dt = _lastFrameMs < 0 ? 16.0 : Math.Clamp(now - _lastFrameMs, 0.5, 100);
        _lastFrameMs = now;

        if (Tick(dt)) RequestFrame();
        else _lastFrameMs = -1;
    }

    /// <summary>推进一帧滚动缓动与指针平滑；返回是否还需要下一帧。</summary>
    internal bool Tick(double dtMs)
    {
        var scrolling = StepScroll(dtMs);
        var tracking = StepPointer(dtMs);
        return scrolling || tracking;
    }

    private bool StepScroll(double dtMs)
    {
        var delta = _targetScroll - _scroll;
        var changed = false;

        if (Math.Abs(delta) >= 0.5)
        {
            _scroll += delta * (1 - Math.Exp(-dtMs / 83.0));
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
            changed = true;
        }

        if (Math.Abs(_targetScroll - _scroll) < 0.5 && _scroll != _targetScroll)
        {
            _scroll = _targetScroll;
            changed = true;
        }

        if (changed) InvalidateVisual();
        return Math.Abs(_targetScroll - _scroll) >= 0.5;
    }

    /// <summary>对指针位置作轻微缓动，使景深与放大平滑过渡；大跨度移动直接落位。</summary>
    private bool StepPointer(double dtMs)
    {
        var dx = _pointer.X - _pointerRender.X;
        var dy = _pointer.Y - _pointerRender.Y;
        var distance = Math.Abs(dx) + Math.Abs(dy);

        if (_pointer.X < -1000 || distance < 0.5 || distance > 300)
        {
            if (_pointerRender != _pointer)
            {
                _pointerRender = _pointer;
                InvalidateVisual();
            }
            return false;
        }

        var factor = 1 - Math.Exp(-dtMs / 40.0);
        _pointerRender = new Point(
            _pointerRender.X + dx * factor,
            _pointerRender.Y + dy * factor);
        InvalidateVisual();
        return true;
    }

    internal double ScrollOffset => _scroll;
    internal double TargetScrollOffset => _targetScroll;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastFrameMs = -1;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _lastFrameMs = -1;
        base.OnDetachedFromVisualTree(e);
    }

    private async Task LoadAsync(AssetListItem item)
    {
        if (ThumbnailProvider is null ||
            _textures.ContainsKey(item.Sha256) ||
            _failed.Contains(item.Sha256) ||
            !_loading.Add(item.Sha256))
        {
            return;
        }

        try
        {
            var bitmap = await ThumbnailProvider.GetAsync(item, 256);
            if (bitmap is not null)
            {
                StoreTexture(item.Sha256, bitmap);
                Dispatcher.UIThread.Post(InvalidateVisual);
            }
            else
            {
                _failed.Add(item.Sha256);
            }
        }
        catch
        {
            _failed.Add(item.Sha256);
        }
        finally
        {
            _loading.Remove(item.Sha256);
        }
    }

    private void RequestBlurred(AssetListItem item)
    {
        // 等清晰缩略图就绪后再生成模糊图，避免同一缓存文件被并发生成。
        if (ThumbnailProvider is null ||
            !_textures.ContainsKey(item.Sha256) ||
            _blurred.ContainsKey(item.Sha256) ||
            _blurFailed.Contains(item.Sha256) ||
            !_loadingBlur.Add(item.Sha256))
        {
            return;
        }

        _ = LoadBlurredAsync(item);
    }

    private async Task LoadBlurredAsync(AssetListItem item)
    {
        var acquired = false;
        try
        {
            await _blurGate.WaitAsync();
            acquired = true;
            var bitmap = await ThumbnailProvider!.GetBlurredAsync(item, 32);
            if (bitmap is not null)
            {
                StoreBlurred(item.Sha256, bitmap);
                Dispatcher.UIThread.Post(InvalidateVisual);
            }
            else
            {
                _blurFailed.Add(item.Sha256);
            }
        }
        catch
        {
            _blurFailed.Add(item.Sha256);
        }
        finally
        {
            if (acquired) _blurGate.Release();
            _loadingBlur.Remove(item.Sha256);
        }
    }

    private void StoreTexture(string key, Bitmap bitmap)
    {
        if (_textures.TryGetValue(key, out var previous))
            _textureBytes -= TextureBytes(previous);

        _textures[key] = bitmap;
        _textureBytes += TextureBytes(bitmap);

        if (_lruIndex.TryGetValue(key, out var existing))
            _lru.Remove(existing);
        _lruIndex[key] = _lru.AddLast(key);

        while (_textureBytes > MaxTextureBytes && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            _lruIndex.Remove(oldest.Value);
            if (_textures.Remove(oldest.Value, out var evicted))
                _textureBytes -= TextureBytes(evicted);
        }
    }

    private void StoreBlurred(string key, Bitmap bitmap)
    {
        if (_blurred.TryGetValue(key, out var previous))
            _blurredBytes -= TextureBytes(previous);

        _blurred[key] = bitmap;
        _blurredBytes += TextureBytes(bitmap);

        if (_blurLruIndex.TryGetValue(key, out var existing))
            _blurLru.Remove(existing);
        _blurLruIndex[key] = _blurLru.AddLast(key);

        while (_blurredBytes > MaxBlurredBytes && _blurLru.First is { } oldest)
        {
            _blurLru.RemoveFirst();
            _blurLruIndex.Remove(oldest.Value);
            if (_blurred.Remove(oldest.Value, out var evicted))
                _blurredBytes -= TextureBytes(evicted);
        }
    }

    private void TouchTexture(string key)
    {
        if (!_lruIndex.TryGetValue(key, out var node)) return;
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void TouchBlurred(string key)
    {
        if (!_blurLruIndex.TryGetValue(key, out var node)) return;
        _blurLru.Remove(node);
        _blurLru.AddLast(node);
    }

    private static long TextureBytes(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        return (long)size.Width * size.Height * 4;
    }
}
