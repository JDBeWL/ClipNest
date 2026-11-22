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
/// 自适应瀑布流图库，按图片真实宽高比排布并虚拟化绘制，
/// 提供 Eagle 风格的悬停操作、单击与区间多选、双击打开、右键菜单与键盘导航。
/// </summary>
public sealed class WaterfallGallery : Control
{
    public static readonly StyledProperty<IReadOnlyList<AssetListItem>?> ItemsProperty =
        AvaloniaProperty.Register<WaterfallGallery, IReadOnlyList<AssetListItem>?>(nameof(Items));

    public static readonly StyledProperty<double> TileWidthProperty =
        AvaloniaProperty.Register<WaterfallGallery, double>(
            nameof(TileWidth), 200, coerce: (_, value) => Math.Clamp(value, 132, 340));

    /// <summary>
    /// 等高方格，用于按相关度排序的检索结果。等高的列会让最短列优先退化成逐行排列，
    /// 名次因此从左到右、从上到下可读，而不是在瀑布流中交错分布。
    /// </summary>
    public static readonly StyledProperty<bool> UniformTilesProperty =
        AvaloniaProperty.Register<WaterfallGallery, bool>(nameof(UniformTiles));

    private const double OpenButtonSize = 26;
    private const double ScrollBarWidth = 4;
    private const double TileRadius = 14;
    private const double HoverScaleTarget = 1.025;
    private const int MaxConcurrentLoads = 6;

    // 图库不限制条数后，纹理缓存必须自行封顶，否则滚过整库会一直占着位图。
    private const long MaxTextureBytes = 192L * 1024 * 1024;

    // 瓦片最高约 340×755 逻辑像素，2 倍屏下 1536 的长边足够覆盖；再大只会让常驻位图翻倍。
    private const int MaxThumbnailSize = 1536;

    // 画布本身透明。内容层的实色底由外层提供，避免出现第二层背景色差。
    // 颜色统一走 ThemeColors，浅色模式复用同一份绘制代码。
    private static Color BackgroundColor => ThemeColors.SurfaceCanvas;
    private static Color PlaceholderColor => ThemeColors.SurfaceElevated;
    private static Color PlaceholderLineColor => ThemeColors.BorderStrong;
    // 缩略图上压着的是恒定的深色遮罩，文字与图标必须跟着恒定为浅色，
    // 否则浅色主题下会变成深字压深底。
    private static Color OverlayTextColor => ThemeColors.OverlayText;
    private static Color AccentColor => ThemeColors.Accent;
    private static Color HoverOutlineColor => ThemeColors.HoverOutline;
    private static Color OpenButtonColor => ThemeColors.OverlayButton;
    private static Color ScrimColor => ThemeColors.TileScrim;

    private readonly Dictionary<string, Bitmap> _textures = new();
    private readonly Dictionary<string, int> _textureSizes = new();
    private readonly Dictionary<string, double> _measuredRatios = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new();
    private readonly HashSet<string> _loading = new();
    private readonly HashSet<string> _failed = new();
    private long _textureBytes;
    private readonly SemaphoreSlim _loadGate = new(MaxConcurrentLoads, MaxConcurrentLoads);

    private int _themeVersion = ThemeColors.Version;
    private IBrush _background = new SolidColorBrush(BackgroundColor);
    private IBrush _placeholder = new SolidColorBrush(PlaceholderColor);
    private IPen _placeholderPen = new Pen(new SolidColorBrush(PlaceholderLineColor), 1.4);
    private IBrush _overlayText = new SolidColorBrush(OverlayTextColor);
    private IBrush _scrim = Brushes.Transparent;
    private IBrush _openButton = new SolidColorBrush(OpenButtonColor);
    private IBrush _scrollThumb = new SolidColorBrush(ThemeColors.ScrollThumb);
    private IBrush _scrollThumbActive = new SolidColorBrush(ThemeColors.ScrollThumbHover);
    private IPen _hoverPen = new Pen(new SolidColorBrush(HoverOutlineColor), 1);

    // 选中态用三层低透明度描边叠出柔光，再描一条细亮边，避免硬蓝框。
    private IPen _selectionGlowPen = new Pen(new SolidColorBrush(Alpha(ThemeColors.Accent, 0x1F)), 6);
    private IPen _selectionHaloPen = new Pen(new SolidColorBrush(Alpha(ThemeColors.Accent, 0x33)), 2.4);
    private IPen _selectionRimPen = new Pen(new SolidColorBrush(Alpha(ThemeColors.AccentBright, 0xD9)), 1.1);

    // 磨砂勾选徽章，半透明白底、淡描边与蓝色勾。
    private readonly IBrush _badgeShadow = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x00, 0x00));
    private readonly IBrush _badgeFill = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
    private readonly IPen _badgeRim = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1);
    private readonly IPen _checkPen = new Pen(new SolidColorBrush(AccentColor), 1.9);
    private IPen _iconPen = new Pen(new SolidColorBrush(OverlayTextColor), 1.4);
    private readonly Typeface _typeface = new(AppFonts.Ui);
    private bool _frameScheduled;
    private double _lastFrameMs = -1;

    private readonly List<TileInfo> _layout = new();
    private List<TileInfo>[] _columns = Array.Empty<List<TileInfo>>();
    private double _layoutTileWidth;
    private readonly HashSet<long> _selected = new();
    private readonly List<long> _selectionOrder = new();

    private INotifyCollectionChanged? _observedItems;
    private bool _layoutDirty = true;
    private double _scroll;
    private double _targetScroll;
    private double _contentHeight;
    private long? _primaryId;
    private long? _anchorId;
    private Point _pointer = new(-10000, -10000);
    private Rect _hoverOpenRect;
    private int _hoverTileIndex = -1;
    private double _hoverScale = 1;
    private bool _draggingScrollBar;
    private double _scrollBarStartY;
    private double _scrollBarStartScroll;
    private Cursor? _handCursor;
    private Cursor? _resizeCursor;

    private readonly struct TileInfo
    {
        public TileInfo(int index, Rect rect)
        {
            Index = index;
            Rect = rect;
        }

        public int Index { get; }
        public Rect Rect { get; }
    }

    public WaterfallGallery()
    {
        ClipToBounds = true;
        Focusable = true;

        BuildScrim();
    }

    /// <summary>文件名遮罩从透明过渡到底色，避免出现一条生硬的横带。</summary>
    private void BuildScrim()
    {
        var color = ScrimColor;
        _scrim = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 0),
                new GradientStop(color, 1)
            }
        };
    }

    private static Color Alpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>主题变化后重建缓存画刷。绘制前调用，开销只有一次版本比较。</summary>
    private void EnsureThemeBrushes()
    {
        if (_themeVersion == ThemeColors.Version) return;
        _themeVersion = ThemeColors.Version;

        _background = new SolidColorBrush(BackgroundColor);
        _placeholder = new SolidColorBrush(PlaceholderColor);
        _placeholderPen = new Pen(new SolidColorBrush(PlaceholderLineColor), 1.4);
        _overlayText = new SolidColorBrush(OverlayTextColor);
        _openButton = new SolidColorBrush(OpenButtonColor);
        _scrollThumb = new SolidColorBrush(ThemeColors.ScrollThumb);
        _scrollThumbActive = new SolidColorBrush(ThemeColors.ScrollThumbHover);
        _hoverPen = new Pen(new SolidColorBrush(HoverOutlineColor), 1);
        _selectionGlowPen = new Pen(new SolidColorBrush(Alpha(ThemeColors.Accent, 0x1F)), 6);
        _selectionHaloPen = new Pen(new SolidColorBrush(Alpha(ThemeColors.Accent, 0x33)), 2.4);
        _selectionRimPen = new Pen(new SolidColorBrush(Alpha(ThemeColors.AccentBright, 0xD9)), 1.1);
        BuildScrim();
    }

    public IReadOnlyList<AssetListItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double TileWidth
    {
        get => GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public bool UniformTiles
    {
        get => GetValue(UniformTilesProperty);
        set => SetValue(UniformTilesProperty, value);
    }

    public IThumbnailProvider? ThumbnailProvider { get; set; }
    public double Gap { get; set; } = 14;

    /// <summary>
    /// 右键菜单由窗口注入。批量复制、移动与删除需要仓储和确认对话框，
    /// 控件只负责在正确的选中状态下打开菜单。
    /// </summary>
    public Func<AssetListItem, ContextMenu?>? ContextMenuFactory { get; set; }

    /// <summary>
    /// 选中描边画在瓦片内侧，所以左右不需要额外留白；
    /// 右侧只留出滚动条槽位，保证瓦片宽度与标题对齐。
    /// </summary>
    public double LeftPadding { get; set; }
    public double RightGutter { get; set; } = 10;
    public double TopPadding { get; set; }
    public double BottomPadding { get; set; } = 92;

    public event EventHandler<AssetListItem>? ItemActivated;
    public event EventHandler<IReadOnlyList<AssetListItem>>? SelectionChanged;
    public event EventHandler<double>? ZoomRequested;

    public IReadOnlyList<AssetListItem> Selection
    {
        get
        {
            var items = Items;
            if (items is null || _selected.Count == 0) return Array.Empty<AssetListItem>();

            // 主选中项放在末尾，让详情栏始终显示最后操作的那一张。
            var result = new List<AssetListItem>(_selected.Count);
            AssetListItem? primary = null;
            foreach (var item in items)
            {
                if (!_selected.Contains(item.Id)) continue;
                if (item.Id == _primaryId) primary = item;
                else result.Add(item);
            }

            if (primary is not null) result.Add(primary);
            return result;
        }
    }

    /// <summary>由详情栏或其他视图回写主选中项，不破坏多选。</summary>
    public void SyncPrimary(long? id)
    {
        if (id is null)
        {
            if (_selected.Count > 0) ClearSelection(notify: false);
            return;
        }

        if (_selected.Count > 1 && _selected.Contains(id.Value)) return;
        if (_selected.Count == 1 && _selected.Contains(id.Value)) return;

        SetSingleSelection(id.Value, notify: false);
    }

    public void ClearSelection(bool notify = true)
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        _selectionOrder.Clear();
        _primaryId = null;
        _anchorId = null;
        InvalidateVisual();
        if (notify) RaiseSelectionChanged();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            if (_observedItems is not null)
                _observedItems.CollectionChanged -= OnItemsCollectionChanged;
            _observedItems = Items as INotifyCollectionChanged;
            if (_observedItems is not null)
                _observedItems.CollectionChanged += OnItemsCollectionChanged;
            MarkLayoutDirty(resetScroll: true);
        }
        else if (change.Property == TileWidthProperty)
        {
            MarkLayoutDirty(resetScroll: false);
        }
        else if (change.Property == UniformTilesProperty)
        {
            MarkLayoutDirty(resetScroll: true);
        }
        else if (change.Property == IsVisibleProperty)
        {
            // 切到光墙或星系后不再逐帧重绘；回来时把未完成的缓动接上。
            if (change.GetNewValue<bool>()) RequestFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastFrameMs = -1;
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _lastFrameMs = -1;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        MarkLayoutDirty(resetScroll: false);
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MarkLayoutDirty(resetScroll: e.Action == NotifyCollectionChangedAction.Reset);
    }

    private void MarkLayoutDirty(bool resetScroll)
    {
        _layoutDirty = true;
        _hoverTileIndex = -1;
        if (resetScroll)
        {
            _scroll = 0;
            _targetScroll = 0;
        }

        InvalidateVisual();
        RequestFrame();
    }


    private void EnsureLayout()
    {
        if (!_layoutDirty) return;
        _layoutDirty = false;
        _layout.Clear();

        var items = Items;
        if (items is null || items.Count == 0 || Bounds.Width <= 1)
        {
            _contentHeight = 0;
            _columns = Array.Empty<List<TileInfo>>();
            ClampScroll();
            return;
        }

        var contentWidth = Math.Max(1, Bounds.Width - LeftPadding - RightGutter);
        var columns = Math.Max(1, (int)Math.Round((contentWidth + Gap) / (TileWidth + Gap)));
        var tileWidth = Math.Max(1, (contentWidth - Gap * (columns - 1)) / columns);
        var columnTops = new double[columns];
        for (var i = 0; i < columns; i++) columnTops[i] = TopPadding;

        var columnTiles = new List<TileInfo>[columns];
        for (var i = 0; i < columns; i++) columnTiles[i] = new List<TileInfo>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var tileHeight = UniformTiles
                ? Math.Round(tileWidth)
                : Math.Max(48, Math.Round(tileWidth / GetRatio(item)));

            var column = 0;
            for (var c = 1; c < columns; c++)
            {
                if (columnTops[c] < columnTops[column]) column = c;
            }

            var rect = new Rect(
                LeftPadding + column * (tileWidth + Gap),
                columnTops[column],
                tileWidth,
                tileHeight);
            var tile = new TileInfo(index, rect);
            _layout.Add(tile);
            columnTiles[column].Add(tile);
            columnTops[column] += tileHeight + Gap;
        }

        _columns = columnTiles;
        _layoutTileWidth = tileWidth;

        var tallest = 0.0;
        for (var c = 0; c < columns; c++) tallest = Math.Max(tallest, columnTops[c]);
        _contentHeight = Math.Max(0, tallest - Gap + BottomPadding);
        ClampScroll();
    }

    private double GetRatio(AssetListItem item)
    {
        var width = item.Width ?? 0;
        var height = item.Height ?? 0;
        if (width > 0 && height > 0) return Math.Clamp(width / (double)height, 0.45, 2.2);
        if (_measuredRatios.TryGetValue(item.Sha256, out var measured))
            return Math.Clamp(measured, 0.45, 2.2);
        return 1.0;
    }

    private double MaxScroll => Math.Max(0, _contentHeight - Bounds.Height);

    private void ClampScroll()
    {
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        _targetScroll = Math.Clamp(_targetScroll, 0, MaxScroll);
    }


    public override void Render(DrawingContext context)
    {
        EnsureThemeBrushes();
        context.FillRectangle(_background, new Rect(Bounds.Size));
        EnsureLayout();

        var items = Items;
        if (items is null || items.Count == 0 || _layout.Count == 0) return;

        var viewport = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var hasPointer = _pointer.X > -1000;
        _hoverOpenRect = default;

        // 悬停项最后绘制并放大，避免被相邻瓦片盖住。
        _hoverTileIndex = hasPointer ? HitTestIndex(_pointer) : -1;
        TileInfo? lifted = null;

        var (first, last) = GetVisibleRange(_scroll, _scroll + Bounds.Height);
        for (var i = first; i <= last && i < _layout.Count; i++)
        {
            var tile = _layout[i];
            var rect = tile.Rect.Translate(new Vector(0, -_scroll));
            if (!rect.Intersects(viewport)) continue;

            if (tile.Index == _hoverTileIndex && _hoverScale > 1.001)
            {
                lifted = tile;
                continue;
            }

            DrawTile(context, items, tile, rect);
        }

        if (lifted is { } hoverTile && _hoverScale > 1.001)
        {
            var rect = hoverTile.Rect.Translate(new Vector(0, -_scroll));

            // 以瓦片中心放大，模拟卡片受视线聚焦时轻微浮起。
            var scale = _hoverScale;
            var center = rect.Center;
            var matrix = new Matrix(
                scale, 0,
                0, scale,
                center.X * (1 - scale),
                center.Y * (1 - scale));
            using (context.PushTransform(matrix))
            {
                DrawTile(context, items, hoverTile, rect);
            }
        }

        DrawScrollBar(context);
    }

    private void DrawTile(DrawingContext context, IReadOnlyList<AssetListItem> items, TileInfo tile, Rect rect)
    {
        var item = items[tile.Index];
        var hover = _pointer.X > -1000 && rect.Contains(_pointer);
        var selected = _selected.Contains(item.Id);

        DrawThumbnail(context, item, rect);

        if (hover)
        {
            // 选中项的边框已由选中环表达，这里只叠悬停信息层。
            if (!selected)
                context.DrawRectangle(null, _hoverPen, rect.Deflate(0.5), TileRadius, TileRadius);
            DrawHoverChrome(context, item, rect);
        }

        if (selected)
        {
            // 光晕必须晚于缩略图与悬停层绘制，否则会被盖住。
            context.DrawRectangle(null, _selectionGlowPen, rect.Inflate(3), TileRadius + 3, TileRadius + 3);
            context.DrawRectangle(null, _selectionHaloPen, rect.Inflate(1.2), TileRadius + 1.2, TileRadius + 1.2);
            context.DrawRectangle(null, _selectionRimPen, rect.Deflate(0.8), TileRadius - 0.8, TileRadius - 0.8);
            DrawCheckBadge(context, rect);
        }
    }

    private int HitTestIndex(Point point)
    {
        if (_columns.Length == 0 || _layoutTileWidth <= 0) return -1;

        var x = point.X - LeftPadding;
        if (x < 0) return -1;

        var column = (int)(x / (_layoutTileWidth + Gap));
        if (column < 0 || column >= _columns.Length) return -1;

        var tiles = _columns[column];
        if (tiles.Count == 0) return -1;

        // 同一列按 Y 递增，先二分到所在行，再确认点确实落在瓦片内。
        var y = point.Y + _scroll;
        int lo = 0, hi = tiles.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tiles[mid].Rect.Top <= y) lo = mid + 1;
            else hi = mid;
        }

        var index = lo - 1;
        if (index < 0) return -1;

        var tile = tiles[index];
        return tile.Rect.Contains(new Point(point.X, y)) ? tile.Index : -1;
    }

    private (int First, int Last) GetVisibleRange(double top, double bottom)
    {
        var first = int.MaxValue;
        var last = -1;

        foreach (var column in _columns)
        {
            if (column.Count == 0) continue;

            var start = LowerBoundByBottom(column, top);
            if (start >= column.Count) continue;

            var end = UpperBoundByTop(column, bottom) - 1;
            if (end < start) continue;

            first = Math.Min(first, column[start].Index);
            last = Math.Max(last, column[end].Index);
        }

        return last < 0 ? (0, -1) : (first, last);
    }

    private static int LowerBoundByBottom(List<TileInfo> tiles, double top)
    {
        int lo = 0, hi = tiles.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tiles[mid].Rect.Bottom <= top) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static int UpperBoundByTop(List<TileInfo> tiles, double bottom)
    {
        int lo = 0, hi = tiles.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tiles[mid].Rect.Top < bottom) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private void DrawThumbnail(DrawingContext context, AssetListItem item, Rect imageRect)
    {
        using (context.PushClip(new RoundedRect(imageRect, TileRadius)))
        {
            if (_textures.TryGetValue(item.Sha256, out var bitmap))
            {
                TouchTexture(item.Sha256);
                var source = GetCenterCrop(
                    new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                    imageRect.Width / Math.Max(1, imageRect.Height));

                // 缩略图就绪即按原亮度绘制；首屏会同时补齐整面墙，淡入会让画廊短暂整体发暗。
                context.DrawImage(bitmap, source, imageRect);
            }
            else
            {
                context.FillRectangle(_placeholder, imageRect);
                DrawPlaceholderGlyph(context, imageRect);
            }
        }

        _ = EnsureTextureAsync(item, imageRect);
    }

    private void DrawHoverChrome(DrawingContext context, AssetListItem item, Rect imageRect)
    {
        using (context.PushClip(new RoundedRect(imageRect, TileRadius)))
        {
            const double band = 46;
            var scrim = new Rect(imageRect.X, imageRect.Bottom - band, imageRect.Width, band);
            context.FillRectangle(_scrim, scrim);

            // 严格单行。长文件名用省略号收尾，不换行，否则第二行会被瓦片底边切掉。
            var name = new FormattedText(
                item.FileName,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _typeface,
                12,
                _overlayText)
            {
                MaxTextWidth = Math.Max(10, imageRect.Width - 20),
                MaxTextHeight = 18,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            context.DrawText(name, new Point(imageRect.X + 10, imageRect.Bottom - 10 - name.Height));
        }

        var openRect = new Rect(
            imageRect.Right - OpenButtonSize - 8,
            imageRect.Y + 8,
            OpenButtonSize,
            OpenButtonSize);
        _hoverOpenRect = openRect;
        context.DrawRectangle(_openButton, null, openRect, 9, 9);
        DrawExpandGlyph(context, openRect);
    }

    private void DrawPlaceholderGlyph(DrawingContext context, Rect rect)
    {
        var width = Math.Min(30, rect.Width * 0.24);
        var height = width * 0.76;
        var box = new Rect(
            rect.Center.X - width / 2,
            rect.Center.Y - height / 2,
            width,
            height);
        context.DrawRectangle(null, _placeholderPen, box, 3, 3);
        context.DrawEllipse(
            _placeholderPen.Brush, null,
            new Point(box.X + width * 0.26, box.Y + height * 0.26),
            width * 0.06, width * 0.06);
        context.DrawLine(
            _placeholderPen,
            new Point(box.X + 3, box.Bottom - 4),
            new Point(box.X + width * 0.42, box.Y + height * 0.48));
        context.DrawLine(
            _placeholderPen,
            new Point(box.X + width * 0.42, box.Y + height * 0.48),
            new Point(box.Right - 3, box.Bottom - 4));
    }

    private void DrawCheckBadge(DrawingContext context, Rect imageRect)
    {
        var center = new Point(imageRect.X + 18, imageRect.Y + 18);

        // 投影在下、半透明白在上，呈现为磨砂玻璃徽章。
        context.DrawEllipse(_badgeShadow, null, center, 12, 12);
        context.DrawEllipse(_badgeFill, _badgeRim, center, 10, 10);

        context.DrawLine(
            _checkPen,
            new Point(center.X - 3.8, center.Y + 0.1),
            new Point(center.X - 1.0, center.Y + 2.9));
        context.DrawLine(
            _checkPen,
            new Point(center.X - 1.0, center.Y + 2.9),
            new Point(center.X + 4.0, center.Y - 2.5));
    }

    private void DrawExpandGlyph(DrawingContext context, Rect button)
    {
        var cx = button.Center.X;
        var cy = button.Center.Y;
        const double arm = 4.2;
        const double inset = 2.6;

        // 四角内折线，表达「在查看器中打开」。
        context.DrawLine(_iconPen, new Point(cx - arm, cy - arm + inset), new Point(cx - arm + inset, cy - arm + inset));
        context.DrawLine(_iconPen, new Point(cx - arm + inset, cy - arm + inset), new Point(cx - arm + inset, cy - arm));
        context.DrawLine(_iconPen, new Point(cx + arm - inset, cy - arm + inset), new Point(cx + arm, cy - arm + inset));
        context.DrawLine(_iconPen, new Point(cx + arm - inset, cy - arm + inset), new Point(cx + arm - inset, cy - arm));
        context.DrawLine(_iconPen, new Point(cx - arm, cy + arm - inset), new Point(cx - arm + inset, cy + arm - inset));
        context.DrawLine(_iconPen, new Point(cx - arm + inset, cy + arm - inset), new Point(cx - arm + inset, cy + arm));
        context.DrawLine(_iconPen, new Point(cx + arm - inset, cy + arm - inset), new Point(cx + arm, cy + arm - inset));
        context.DrawLine(_iconPen, new Point(cx + arm - inset, cy + arm - inset), new Point(cx + arm - inset, cy + arm));
    }

    private void DrawScrollBar(DrawingContext context)
    {
        if (MaxScroll <= 0.5 || Bounds.Height < 80) return;

        var trackTop = 6.0;
        var trackHeight = Bounds.Height - 12;
        var thumbHeight = Math.Max(34, trackHeight * (Bounds.Height / Math.Max(1, _contentHeight)));
        thumbHeight = Math.Min(thumbHeight, trackHeight);
        var progress = _scroll / MaxScroll;
        var thumbTop = trackTop + (trackHeight - thumbHeight) * progress;
        var rect = new Rect(Bounds.Width - 7, thumbTop, ScrollBarWidth, thumbHeight);
        var active = _draggingScrollBar || (_pointer.X > Bounds.Width - 18 &&
                                            _pointer.Y > thumbTop - 6 && _pointer.Y < thumbTop + thumbHeight + 6);

        context.DrawRectangle(active ? _scrollThumbActive : _scrollThumb, null, rect, 2, 2);
    }

    private static Rect GetCenterCrop(Size source, double targetRatio)
    {
        if (source.Width <= 0 || source.Height <= 0) return new Rect(source);
        var sourceRatio = source.Width / source.Height;
        if (sourceRatio > targetRatio)
        {
            var width = source.Height * targetRatio;
            return new Rect((source.Width - width) / 2, 0, width, source.Height);
        }

        var height = source.Width / targetRatio;
        return new Rect(0, (source.Height - height) / 2, source.Width, height);
    }


    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        EnsureLayout();
        _hoverTileIndex = HitTestIndex(_pointer);

        if (_draggingScrollBar)
        {
            var trackHeight = Math.Max(1, Bounds.Height - 12);
            var thumbHeight = Math.Max(34, trackHeight * (Bounds.Height / Math.Max(1, _contentHeight)));
            thumbHeight = Math.Min(thumbHeight, trackHeight);
            var draggable = Math.Max(1, trackHeight - thumbHeight);
            var delta = (_pointer.Y - _scrollBarStartY) / draggable * MaxScroll;
            _scroll = Math.Clamp(_scrollBarStartScroll + delta, 0, MaxScroll);
            _targetScroll = _scroll;
        }

        UpdateCursor();
        InvalidateVisual();
        RequestFrame();
        base.OnPointerMoved(e);
    }

    private void UpdateCursor()
    {
        if (_draggingScrollBar)
        {
            _resizeCursor ??= new Cursor(StandardCursorType.SizeNorthSouth);
            Cursor = _resizeCursor;
            return;
        }

        var hoverOpen = _hoverOpenRect.Width > 0 && _hoverOpenRect.Contains(_pointer);
        if (hoverOpen || (MaxScroll > 0 && _pointer.X > Bounds.Width - 12))
        {
            _handCursor ??= new Cursor(StandardCursorType.Hand);
            Cursor = _handCursor;
            return;
        }

        Cursor = Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetPosition(this);
        _pointer = point;
        var properties = e.GetCurrentPoint(this).Properties;

        EnsureLayout();

        if (properties.IsRightButtonPressed)
        {
            var rightItem = HitTest(point);
            if (rightItem is not null)
            {
                if (!_selected.Contains(rightItem.Id)) SetSingleSelection(rightItem.Id);
                ShowContextMenu(rightItem);
            }

            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed) return;

        // 滚动条优先于瓦片点击。
        if (point.X > Bounds.Width - 16 && MaxScroll > 0)
        {
            var trackHeight = Math.Max(1, Bounds.Height - 12);
            var thumbHeight = Math.Max(34, trackHeight * (Bounds.Height / Math.Max(1, _contentHeight)));
            thumbHeight = Math.Min(thumbHeight, trackHeight);
            var thumbTop = 6 + (trackHeight - thumbHeight) * (_scroll / MaxScroll);
            if (point.Y >= thumbTop && point.Y <= thumbTop + thumbHeight)
            {
                _draggingScrollBar = true;
                _scrollBarStartY = point.Y;
                _scrollBarStartScroll = _scroll;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            var ratio = Math.Clamp(
                (point.Y - 6 - thumbHeight / 2) / Math.Max(1, trackHeight - thumbHeight), 0, 1);
            _scroll = ratio * MaxScroll;
            _targetScroll = _scroll;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var item = HitTest(point);
        if (item is null)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            ItemActivated?.Invoke(this, item);
            e.Handled = true;
            return;
        }

        if (_hoverOpenRect.Contains(point))
        {
            SetSingleSelection(item.Id);
            ItemActivated?.Invoke(this, item);
            e.Handled = true;
            return;
        }

        HandleSelectionClick(item, e.KeyModifiers);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingScrollBar)
        {
            _draggingScrollBar = false;
            InvalidateVisual();
        }

        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _draggingScrollBar = false;
        InvalidateVisual();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointer = new Point(-10000, -10000);
        _hoverOpenRect = default;
        _hoverTileIndex = -1;
        Cursor = Cursor.Default;
        InvalidateVisual();
        RequestFrame();
        base.OnPointerExited(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomRequested?.Invoke(this, Math.Sign(e.Delta.Y) * 14);
            e.Handled = true;
            return;
        }

        var step = Math.Abs(e.Delta.Y) >= 3 ? e.Delta.Y * 26 : e.Delta.Y * 84;
        _targetScroll = Math.Clamp(_targetScroll - step, 0, MaxScroll);
        InvalidateVisual();
        RequestFrame();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Items is null || Items.Count == 0) return;

        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                MoveSelection(e.Key);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                if (PrimaryItem is { } primary)
                {
                    ItemActivated?.Invoke(this, primary);
                    e.Handled = true;
                }
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                ClearSelection();
                e.Handled = true;
                break;
            case Key.PageDown:
                _targetScroll = Math.Clamp(_targetScroll + Bounds.Height * 0.85, 0, MaxScroll);
                RequestFrame();
                e.Handled = true;
                break;
            case Key.PageUp:
                _targetScroll = Math.Clamp(_targetScroll - Bounds.Height * 0.85, 0, MaxScroll);
                RequestFrame();
                e.Handled = true;
                break;
            case Key.Home:
                _targetScroll = 0;
                RequestFrame();
                e.Handled = true;
                break;
            case Key.End:
                _targetScroll = MaxScroll;
                RequestFrame();
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    private AssetListItem? PrimaryItem =>
        _primaryId is null ? null : Items?.FirstOrDefault(item => item.Id == _primaryId);

    private void HandleSelectionClick(AssetListItem item, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift) && _anchorId is not null)
        {
            var anchorIndex = IndexOf(_anchorId.Value);
            var targetIndex = IndexOf(item.Id);
            if (anchorIndex >= 0 && targetIndex >= 0)
            {
                var from = Math.Min(anchorIndex, targetIndex);
                var to = Math.Max(anchorIndex, targetIndex);
                _selected.Clear();
                _selectionOrder.Clear();
                for (var i = from; i <= to && i < Items!.Count; i++)
                {
                    _selected.Add(Items[i].Id);
                    _selectionOrder.Add(Items[i].Id);
                }

                _primaryId = item.Id;
                InvalidateVisual();
                RaiseSelectionChanged();
                return;
            }
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            _anchorId = item.Id;
            if (!_selected.Remove(item.Id))
            {
                _selected.Add(item.Id);
                _selectionOrder.Add(item.Id);
                _primaryId = item.Id;
            }
            else
            {
                _selectionOrder.Remove(item.Id);
                _primaryId = _selected.Count > 0 ? _selectionOrder[^1] : null;
                if (_selected.Count == 0) _anchorId = null;
            }

            InvalidateVisual();
            RaiseSelectionChanged();
            return;
        }

        _anchorId = item.Id;
        SetSingleSelection(item.Id);
    }

    private void SetSingleSelection(long id, bool notify = true)
    {
        if (_selected.Count == 1 && _selected.Contains(id) && _primaryId == id)
        {
            if (notify) RaiseSelectionChanged();
            return;
        }

        _selected.Clear();
        _selected.Add(id);
        _selectionOrder.Clear();
        _selectionOrder.Add(id);
        _primaryId = id;
        InvalidateVisual();
        if (notify) RaiseSelectionChanged();
    }

    private void SelectAll()
    {
        if (Items is null) return;
        _selected.Clear();
        _selectionOrder.Clear();
        foreach (var item in Items)
        {
            _selected.Add(item.Id);
            _selectionOrder.Add(item.Id);
        }

        _primaryId = Items.Count > 0 ? Items[0].Id : null;
        _anchorId = _primaryId;
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, Selection);

    private int IndexOf(long id)
    {
        var items = Items;
        if (items is null) return -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id) return i;
        }

        return -1;
    }

    private int FindNeighbor(Rect current, Key key)
    {
        var best = -1;
        var bestScore = double.MaxValue;

        foreach (var tile in _layout)
        {
            var rect = tile.Rect;
            var horizontalOverlap = Math.Min(rect.Right, current.Right) - Math.Max(rect.Left, current.Left);
            var verticalOverlap = Math.Min(rect.Bottom, current.Bottom) - Math.Max(rect.Top, current.Top);
            double score;

            switch (key)
            {
                case Key.Left when rect.Right <= current.Left + 1 && verticalOverlap > 0:
                    score = (current.Left - rect.Right) + Math.Abs(rect.Center.Y - current.Center.Y) * 0.7;
                    break;
                case Key.Right when rect.Left >= current.Right - 1 && verticalOverlap > 0:
                    score = (rect.Left - current.Right) + Math.Abs(rect.Center.Y - current.Center.Y) * 0.7;
                    break;
                case Key.Up when rect.Bottom <= current.Top + 1 && horizontalOverlap > 0:
                    score = (current.Top - rect.Bottom) + Math.Abs(rect.Center.X - current.Center.X) * 0.7;
                    break;
                case Key.Down when rect.Top >= current.Bottom - 1 && horizontalOverlap > 0:
                    score = (rect.Top - current.Bottom) + Math.Abs(rect.Center.X - current.Center.X) * 0.7;
                    break;
                default:
                    continue;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = tile.Index;
            }
        }

        return best;
    }

    private void MoveSelection(Key key)
    {
        EnsureLayout();
        var items = Items;
        if (items is null || items.Count == 0 || _layout.Count == 0) return;

        if (_primaryId is null)
        {
            SetSingleSelection(items[0].Id);
            ScrollIntoView(_layout[0].Rect);
            return;
        }

        var currentIndex = IndexOf(_primaryId.Value);
        var currentTile = _layout.FirstOrDefault(tile => tile.Index == currentIndex);
        if (currentTile.Rect.Width <= 0)
        {
            SetSingleSelection(items[0].Id);
            return;
        }

        var neighbor = FindNeighbor(currentTile.Rect, key);
        if (neighbor < 0 || neighbor >= items.Count) return;

        var target = items[neighbor];
        _anchorId = target.Id;
        SetSingleSelection(target.Id);
        ScrollIntoView(_layout[neighbor].Rect);
    }

    private void ScrollIntoView(Rect contentRect)
    {
        var rect = contentRect.Translate(new Vector(0, -_scroll));
        if (rect.Top < TopPadding)
            _targetScroll = Math.Clamp(_scroll + rect.Top - TopPadding - 8, 0, MaxScroll);
        else if (rect.Bottom > Bounds.Height - 8)
            _targetScroll = Math.Clamp(_scroll + rect.Bottom - Bounds.Height + 8, 0, MaxScroll);
        else
            return;

        RequestFrame();
    }

    private AssetListItem? HitTest(Point point)
    {
        var items = Items;
        var index = HitTestIndex(point);
        return items is not null && index >= 0 && index < items.Count ? items[index] : null;
    }

    private void ShowContextMenu(AssetListItem item)
    {
        var menu = ContextMenuFactory?.Invoke(item) ?? CreateDefaultContextMenu(item);
        menu.Open(this);
    }

    private ContextMenu CreateDefaultContextMenu(AssetListItem item)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => ItemActivated?.Invoke(this, item);
        var reveal = new MenuItem { Header = "在资源管理器中显示" };
        reveal.Click += (_, _) => ExplorerReveal.Reveal(item.PrimaryPath);
        var copy = new MenuItem { Header = "复制文件路径" };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(item.PrimaryPath);
        };

        menu.Items.Add(open);
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        return menu;
    }

    /// <summary>
    /// 请求在合成器的下一帧推进动画。用帧回调而不是 16ms 定时器，
    /// 高刷屏上滚动步进才与刷新节奏对齐，不会出现速度不均的顿挫。
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

        if (StepAnimation(dt)) RequestFrame();
        else _lastFrameMs = -1;
    }

    /// <summary>推进一帧滚动缓动与悬停放大；没有活动时返回 false。</summary>
    internal bool StepAnimation(double dtMs)
    {
        var moving = Math.Abs(_targetScroll - _scroll) > 0.4;
        var hoverTarget = _hoverTileIndex >= 0 ? HoverScaleTarget : 1.0;
        var hoverAnimating = Math.Abs(_hoverScale - hoverTarget) > 0.001;
        if (!moving && !hoverAnimating) return false;

        if (moving)
        {
            _scroll += (_targetScroll - _scroll) * (1 - Math.Exp(-dtMs / 67.0));
            if (Math.Abs(_targetScroll - _scroll) < 0.4) _scroll = _targetScroll;
            EnsureLayout();
            _hoverTileIndex = HitTestIndex(_pointer);
        }

        if (hoverAnimating)
            _hoverScale += (hoverTarget - _hoverScale) * (1 - Math.Exp(-dtMs / 56.0));

        InvalidateVisual();
        return true;
    }

    internal double ScrollOffset => _scroll;
    internal double TargetScrollOffset => _targetScroll;

    /// <summary>
    /// 按瓦片当前的物理像素尺寸取图。缩放条把瓦片放大后，已加载的位图不够覆盖时补一张更大的；
    /// 旧图在新图到达前继续绘制，避免放大过程中出现空白。
    /// </summary>
    private async Task EnsureTextureAsync(AssetListItem item, Rect tileRect)
    {
        if (ThumbnailProvider is null) return;

        var target = RequiredTextureSize(item, tileRect);
        if (target <= 0) return;

        if (_textures.ContainsKey(item.Sha256) &&
            _textureSizes.GetValueOrDefault(item.Sha256) >= target)
        {
            return;
        }

        if (_failed.Contains(item.Sha256) || !_loading.Add(item.Sha256)) return;

        var acquired = false;
        try
        {
            await _loadGate.WaitAsync();
            acquired = true;
            var bitmap = await ThumbnailProvider.GetAsync(item, target);
            if (bitmap is null)
            {
                _failed.Add(item.Sha256);
                return;
            }

            _textureSizes[item.Sha256] = target;
            StoreTexture(item.Sha256, bitmap);

            if (item.Width is null or 0 || item.Height is null or 0)
            {
                var ratio = bitmap.PixelSize.Width / (double)Math.Max(1, bitmap.PixelSize.Height);
                if (!_measuredRatios.ContainsKey(item.Sha256))
                {
                    _measuredRatios[item.Sha256] = ratio;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _layoutDirty = true;
                        InvalidateVisual();
                    });
                }
            }

            Dispatcher.UIThread.Post(InvalidateVisual);
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

    private int RequiredTextureSize(AssetListItem item, Rect tileRect)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = ThumbnailSizing.ForBox(
            tileRect.Width, tileRect.Height, SourceRatio(item), scaling, MaxThumbnailSize);

        // 原图比目标档位还小时按原图取，避免同一张图因档位不同缓存出多份内容相同的副本。
        var native = Math.Max(item.Width ?? 0, item.Height ?? 0);
        return native > 0 ? Math.Min(size, native) : size;
    }

    private double? SourceRatio(AssetListItem item)
    {
        if (item is { Width: > 0, Height: > 0 })
            return item.Width.Value / (double)item.Height.Value;

        return _measuredRatios.TryGetValue(item.Sha256, out var measured) ? measured : null;
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
            _textureSizes.Remove(oldest.Value);
            if (_textures.Remove(oldest.Value, out var evicted))
                _textureBytes -= TextureBytes(evicted);
        }
    }

    private void TouchTexture(string key)
    {
        if (!_lruIndex.TryGetValue(key, out var node)) return;
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private static long TextureBytes(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        return (long)size.Width * size.Height * 4;
    }
}
