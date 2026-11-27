using System.Collections.Specialized;
using System.Globalization;
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

/// <summary>
/// 缩略图网格，虚拟化绘制可见瓦片、中心裁切、悬停与选中反馈、平滑滚动。
/// </summary>
public sealed class ThumbnailGridCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<AssetListItem>?> ItemsProperty =
        AvaloniaProperty.Register<ThumbnailGridCanvas, IReadOnlyList<AssetListItem>?>(nameof(Items));

    private const int MaxConcurrentLoads = 4;
    private const double CaptionHeight = 42;

    private readonly Dictionary<string, Bitmap> _textures = new();
    private readonly HashSet<string> _loading = new();
    private readonly HashSet<string> _failed = new();
    private readonly SemaphoreSlim _loadGate = new(MaxConcurrentLoads, MaxConcurrentLoads);
    private int _themeVersion = ThemeColors.Version;
    private int _fontVersion = AppFonts.Version;
    private IBrush _background = new SolidColorBrush(ThemeColors.SurfaceWindow);
    private IBrush _card = new SolidColorBrush(ThemeColors.SurfaceElevated);
    private IBrush _cardHover = new SolidColorBrush(ThemeColors.SurfaceHover);
    private IBrush _placeholder = new SolidColorBrush(ThemeColors.SurfaceCard);
    private IBrush _text = new SolidColorBrush(ThemeColors.TextPrimary);
    private IBrush _muted = new SolidColorBrush(ThemeColors.TextTertiary);
    private IPen _border = new Pen(new SolidColorBrush(ThemeColors.BorderSubtle), 1);
    private IPen _hoverBorder = new Pen(new SolidColorBrush(ThemeColors.BorderStrong), 1);
    private IPen _selectedBorder = new Pen(new SolidColorBrush(ThemeColors.Accent), 2);
    private IPen _placeholderPen = new Pen(new SolidColorBrush(ThemeColors.BorderStrong), 1.5);
    private IBrush _selectedGlow = new SolidColorBrush(ThemeColors.SelectionFill);
    private Typeface _typeface = new(AppFonts.Ui);
    private readonly DispatcherTimer _timer;

    private double _scroll;
    private double _targetScroll;
    private Point _pointer = new(-10000, -10000);
    private long? _selectedId;
    private AssetListItem? _pressedItem;
    private Point _pressPoint;
    private bool _dragging;
    private double _dragStartY;
    private double _dragScroll;
    private INotifyCollectionChanged? _observedItems;

    public ThumbnailGridCanvas()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    public IReadOnlyList<AssetListItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public IThumbnailProvider? ThumbnailProvider { get; set; }
    public double CornerRadius { get; set; }
    public double MinTileWidth { get; set; } = 190;
    public double Gap { get; set; } = 14;
    public double Padding { get; set; } = 16;

    public event EventHandler<AssetListItem>? TileSelected;
    public event EventHandler<AssetListItem>? TileActivated;

    public void SelectItem(long? id)
    {
        if (_selectedId == id) return;
        _selectedId = id;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ItemsProperty)
        {
            if (_observedItems is not null)
                _observedItems.CollectionChanged -= OnItemsChanged;
            _observedItems = Items as INotifyCollectionChanged;
            if (_observedItems is not null)
                _observedItems.CollectionChanged += OnItemsChanged;
            _scroll = 0;
            _targetScroll = 0;
            InvalidateVisual();
        }

        base.OnPropertyChanged(change);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _textures.Clear();
            _loading.Clear();
            _failed.Clear();
        }

        _targetScroll = Math.Clamp(_targetScroll, 0, MaxScroll);
        InvalidateVisual();
    }

    /// <summary>主题或字体变化后重建缓存画刷与字形。绘制前调用，开销只有两次版本比较。</summary>
    private void EnsureThemeBrushes()
    {
        if (_fontVersion != AppFonts.Version)
        {
            _fontVersion = AppFonts.Version;
            _typeface = new Typeface(AppFonts.Ui);
        }

        if (_themeVersion == ThemeColors.Version) return;
        _themeVersion = ThemeColors.Version;
        _background = new SolidColorBrush(ThemeColors.SurfaceWindow);
        _card = new SolidColorBrush(ThemeColors.SurfaceElevated);
        _cardHover = new SolidColorBrush(ThemeColors.SurfaceHover);
        _placeholder = new SolidColorBrush(ThemeColors.SurfaceCard);
        _text = new SolidColorBrush(ThemeColors.TextPrimary);
        _muted = new SolidColorBrush(ThemeColors.TextTertiary);
        _border = new Pen(new SolidColorBrush(ThemeColors.BorderSubtle), 1);
        _hoverBorder = new Pen(new SolidColorBrush(ThemeColors.BorderStrong), 1);
        _selectedBorder = new Pen(new SolidColorBrush(ThemeColors.Accent), 2);
        _placeholderPen = new Pen(new SolidColorBrush(ThemeColors.BorderStrong), 1.5);
        _selectedGlow = new SolidColorBrush(ThemeColors.SelectionFill);
    }

    public override void Render(DrawingContext context)
    {
        EnsureThemeBrushes();
        using var clip = context.PushClip(
            new RoundedRect(new Rect(Bounds.Size), this.CornerRadius));
        context.FillRectangle(_background, new Rect(Bounds.Size));

        var items = Items;
        if (items is null || items.Count == 0 || Bounds.Width <= 0) return;

        var columns = Columns;
        var tileWidth = TileWidth;
        var tileHeight = TileHeight;
        var imageHeight = ImageHeight;
        var step = tileHeight + Gap;

        var firstRow = Math.Max(0, (int)Math.Floor((_scroll - Padding) / step));
        var lastRow = Math.Min(Rows - 1, (int)Math.Ceiling((_scroll + Bounds.Height - Padding) / step));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                if (index >= items.Count) break;

                var item = items[index];
                var rect = GetTileRect(index, columns, tileWidth, tileHeight);
                if (!rect.Intersects(new Rect(Bounds.Size))) continue;

                var hover = rect.Contains(_pointer);
                var selected = _selectedId == item.Id;
                if (selected)
                {
                    context.DrawRectangle(_selectedGlow, null, rect.Inflate(3), 14, 14);
                }

                context.DrawRectangle(
                    hover ? _cardHover : _card,
                    selected ? _selectedBorder : hover ? _hoverBorder : _border,
                    rect, 12, 12);

                var imageRect = new Rect(rect.X, rect.Y, imageHeight > 0 ? rect.Width : 0, imageHeight);
                DrawImage(context, item, imageRect, hover);

                var name = CreateText(item.FileName, 12, _text, rect.Width - 20);
                context.DrawText(name, new Point(rect.X + 10, rect.Y + imageHeight + 8));

                var date = item.TakenAt?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "";
                var meta = CreateText(date, 10, _muted, rect.Width - 20);
                context.DrawText(meta, new Point(rect.X + 10, rect.Y + imageHeight + 24));

                _ = LoadAsync(item);
            }
        }
    }

    private void DrawImage(
        DrawingContext context, AssetListItem item, Rect imageRect, bool hover)
    {
        using (context.PushGeometryClip(
            new RectangleGeometry(imageRect) { RadiusX = 12, RadiusY = 12 }))
        {
            if (_textures.TryGetValue(item.Sha256, out var bitmap))
            {
                var source = LightWallLayout.GetCenterCrop(
                    new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                    imageRect.Width / Math.Max(1, imageRect.Height));
                context.DrawImage(bitmap, source, imageRect);
            }
            else
            {
                context.FillRectangle(_placeholder, imageRect);

                var size = Math.Min(30, imageRect.Height * 0.28);
                var box = new Rect(
                    imageRect.Center.X - size / 2,
                    imageRect.Center.Y - size * 0.34,
                    size,
                    size * 0.68);
                context.DrawRectangle(null, _placeholderPen, box, 3, 3);
                context.DrawEllipse(
                    _placeholderPen.Brush, null,
                    new Point(box.X + size * 0.28, box.Y + size * 0.24),
                    size * 0.07, size * 0.07);
            }
        }

        if (hover)
        {
            using (context.PushOpacity(0.06))
            {
                context.DrawRectangle(Brushes.White, null, imageRect, 0, 0);
            }
        }
    }

    private FormattedText CreateText(
        string text, double fontSize, IBrush brush, double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            fontSize,
            brush)
        {
            MaxTextWidth = Math.Max(0, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis
        };
        return formatted;
    }

    // 布局。

    private int Columns => Math.Max(
        1, (int)Math.Floor((ContentWidth + Gap) / (MinTileWidth + Gap)));

    private double ContentWidth => Math.Max(1, Bounds.Width - Padding * 2);

    private double TileWidth
        => Math.Max(1, (ContentWidth - Gap * (Columns - 1)) / Columns);

    private double ImageHeight => TileWidth * 0.74;

    private double TileHeight => ImageHeight + CaptionHeight;

    private int Rows => Items is null || Items.Count == 0
        ? 0
        : (int)Math.Ceiling(Items.Count / (double)Columns);

    private double MaxScroll => Rows == 0
        ? 0
        : Math.Max(0, Padding * 2 + Rows * (TileHeight + Gap) - Gap - Bounds.Height);

    private Rect GetTileRect(int index, int columns, double tileWidth, double tileHeight)
    {
        var row = index / columns;
        var col = index % columns;
        return new Rect(
            Padding + col * (tileWidth + Gap),
            Padding + row * (tileHeight + Gap) - _scroll,
            tileWidth,
            tileHeight);
    }

    // 交互。

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        if (_dragging)
        {
            var delta = _pointer.Y - _dragStartY;
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
        _dragStartY = point.Y;
        _dragScroll = _scroll;
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);

        var point = e.GetPosition(this);
        var moved = Math.Abs(point.X - _pressPoint.X) > 4 ||
                    Math.Abs(point.Y - _pressPoint.Y) > 4;
        if (_pressedItem is not null && !moved)
            TileSelected?.Invoke(this, _pressedItem);
        _pressedItem = null;

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _targetScroll = Math.Clamp(_targetScroll - e.Delta.Y * 90, 0, MaxScroll);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointer = new Point(-10000, -10000);
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    private AssetListItem? HitTest(Point point)
    {
        var items = Items;
        if (items is null) return null;

        var columns = Columns;
        for (var i = 0; i < items.Count; i++)
        {
            if (GetTileRect(i, columns, TileWidth, TileHeight).Contains(point))
                return items[i];
        }

        return null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _targetScroll - _scroll;
        if (!_dragging && Math.Abs(delta) < 0.5 && _pointer.X < -1000) return;

        _scroll += delta * 0.2;
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        InvalidateVisual();
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

        var acquired = false;
        try
        {
            await _loadGate.WaitAsync();
            acquired = true;
            var bitmap = await ThumbnailProvider.GetAsync(item, 320);
            if (bitmap is not null)
            {
                _textures[item.Sha256] = bitmap;
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
            if (acquired) _loadGate.Release();
            _loading.Remove(item.Sha256);
        }
    }
}
