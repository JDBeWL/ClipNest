using System.Collections.Specialized;
using System.Diagnostics;
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
/// 标签星系画布，包含确定性星点背景、簇与节点两档 LOD、四叉树裁剪、缩略图 LRU 与相机交互。
/// </summary>
public sealed class GalaxyCanvas : Control
{
    // 低于该倍率只看簇卡与代表图，放大后才展开全部缩略图。
    private const double OverviewZoomThreshold = 0.55;
    private const double NodeSize = GalaxyLayout.NodeSize;
    private const double NodeHalf = NodeSize / 2;
    private const double NodeHitRadius = 70;
    private const int MaxTextures = 500;
    private const int MaxConcurrentLoads = 4;
    private const int StarCount = 160;
    private const double FlightDurationMs = 400;

    private static Color BackgroundColor => ThemeColors.SurfaceCanvas;
    private static Color NodeColor => ThemeColors.SurfaceCard;
    private static Color SelectionColor => ThemeColors.Accent;
    private static Color HoverColor => ThemeColors.AccentHover;
    private static Color DefaultClusterColor => ThemeColors.BorderStrong;
    private static Color LabelColor => ThemeColors.TextSecondary;
    private static Color ClusterLabelColor => ThemeColors.TextPrimary;
    private static Color TooltipTextColor => ThemeColors.TextPrimary;
    private static Color TooltipBackgroundColor => ThemeColors.SurfaceElevated;
    private static Color TooltipBorderColor => ThemeColors.BorderStrong;
    private Typeface _labelTypeface = new(AppFonts.Ui);
    private int _fontVersion = AppFonts.Version;

    public static readonly StyledProperty<IReadOnlyList<GalaxyNode>?> NodesProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlyList<GalaxyNode>?>(nameof(Nodes));

    public static readonly StyledProperty<IReadOnlyList<GalaxyCluster>?> ClustersProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlyList<GalaxyCluster>?>(nameof(Clusters));

    public static readonly StyledProperty<IReadOnlySet<long>?> HighlightedIdsProperty =
        AvaloniaProperty.Register<GalaxyCanvas, IReadOnlySet<long>?>(nameof(HighlightedIds));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<GalaxyCanvas, double>(nameof(CornerRadius));

    private readonly double[] _starX = new double[StarCount];
    private readonly double[] _starY = new double[StarCount];
    private readonly double[] _starR = new double[StarCount];
    private readonly Dictionary<string, Bitmap> _textures = new();
    private readonly HashSet<string> _loading = new();
    private readonly HashSet<string> _failed = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruIndex = new();
    private readonly SemaphoreSlim _loadGate = new(MaxConcurrentLoads, MaxConcurrentLoads);
    private readonly Dictionary<long, GalaxyCluster> _clustersById = new();
    private readonly Dictionary<long, List<GalaxyNode>> _clusterSamples = new();
    private readonly HashSet<long> _hoverTags = new();
    private readonly Stopwatch _flightClock = new();
    private readonly Dictionary<long, (IBrush Fill, IPen Border, IPen Leader)> _clusterStyles = new();
    private readonly Dictionary<long, (IBrush Fill, IPen Border, IPen Leader)> _clusterHoverStyles = new();
    private readonly Dictionary<(string Text, int SizeKey, IBrush Brush), FormattedText> _textCache = new();
    private readonly Dictionary<(int Rating, int SizeKey), FormattedText> _ratingTexts = new();
    private bool _frameScheduled;
    private double _lastFrameMs = -1;
    private bool _zoomActive;
    private double _targetZoom = 1;
    private Point _zoomAnchor;

    private int _themeVersion = ThemeColors.Version;
    private int _localeVersion = AppLocalization.Version;
    private IBrush _backgroundBrush = new SolidColorBrush(BackgroundColor);
    private IBrush _nodeBrush = new SolidColorBrush(NodeColor);
    private IBrush _starBrush = new SolidColorBrush(ThemeColors.Star);
    private IBrush _labelBrush = new SolidColorBrush(LabelColor);
    private IBrush _clusterLabelBrush = new SolidColorBrush(ClusterLabelColor);
    private IBrush _selectionBrush = new SolidColorBrush(SelectionColor);
    private IBrush _hoverBrush = new SolidColorBrush(HoverColor);
    private IBrush _favoriteBrush = new SolidColorBrush(SelectionColor);
    private IBrush _tooltipBrush = new SolidColorBrush(TooltipTextColor);
    private IBrush _tooltipBackground = new SolidColorBrush(TooltipBackgroundColor);
    private IPen _tooltipBorder = new Pen(new SolidColorBrush(TooltipBorderColor), 1);
    private IPen _hoverLabelBorder = new Pen(new SolidColorBrush(HoverColor), 1.5);

    private GalaxyQuadtree? _quadtree;
    private INotifyCollectionChanged? _observedNodes;
    private INotifyCollectionChanged? _observedClusters;
    private Point _pointer = new(-10000, -10000);
    private GalaxyNode? _hoverNode;
    private GalaxyCluster? _hoverCluster;
    private long? _selectedNodeId;
    private bool _needsFit;
    private bool _treeDirty;
    private bool _dragging;
    private Point _pressPoint;
    private double _dragCenterX;
    private double _dragCenterY;
    private GalaxyNode? _pressedNode;
    private GalaxyCluster? _pressedCluster;
    private bool _flying;
    private double _flyFromX;
    private double _flyFromY;
    private double _flyFromZoom;
    private double _flyToX;
    private double _flyToY;
    private double _flyToZoom;

    public GalaxyCanvas()
    {
        ClipToBounds = true;

        // 确定性星点，使用归一化坐标与尺寸，随相机平移产生轻微视差。
        var random = new Random(20261115);
        for (var i = 0; i < StarCount; i++)
        {
            _starX[i] = random.NextDouble();
            _starY[i] = random.NextDouble();
            _starR[i] = 0.5 + random.NextDouble() * 1.1;
        }
    }

    public IReadOnlyList<GalaxyNode>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IReadOnlyList<GalaxyCluster>? Clusters
    {
        get => GetValue(ClustersProperty);
        set => SetValue(ClustersProperty, value);
    }

    public IReadOnlySet<long>? HighlightedIds
    {
        get => GetValue(HighlightedIdsProperty);
        set => SetValue(HighlightedIdsProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public GalaxyCamera Camera { get; } = new();

    public IThumbnailProvider? ThumbnailProvider { get; set; }

    public long? SelectedNodeId => _selectedNodeId;

    internal long? HoveredClusterId => _hoverCluster?.Id;

    public event EventHandler<GalaxyNode>? NodeSelected;
    public event EventHandler<GalaxyNode>? NodeActivated;
    public event EventHandler<GalaxyCluster>? ClusterActivated;

    public void SelectNode(long? id)
    {
        if (_selectedNodeId == id) return;
        _selectedNodeId = id;
        InvalidateVisual();
    }

    /// <summary>回到初始视图，下一次绘制时重新适配全部节点。</summary>
    public void ResetView()
    {
        StopFlight();
        _zoomActive = false;
        _needsFit = true;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == NodesProperty)
        {
            if (_observedNodes is not null)
                _observedNodes.CollectionChanged -= OnNodesChanged;
            _observedNodes = Nodes as INotifyCollectionChanged;
            if (_observedNodes is not null)
                _observedNodes.CollectionChanged += OnNodesChanged;

            ClearTextures();
            _hoverNode = null;
            _hoverCluster = null;
            _hoverTags.Clear();
            _treeDirty = true;
            _needsFit = true;
            InvalidateVisual();
        }
        else if (change.Property == ClustersProperty)
        {
            if (_observedClusters is not null)
                _observedClusters.CollectionChanged -= OnClustersChanged;
            _observedClusters = Clusters as INotifyCollectionChanged;
            if (_observedClusters is not null)
                _observedClusters.CollectionChanged += OnClustersChanged;

            RebuildClusterMap();
        }
        else if (change.Property == HighlightedIdsProperty)
        {
            InvalidateVisual();
        }

        if (change.Property == CornerRadiusProperty) InvalidateVisual();

        if (change.Property == IsVisibleProperty && !change.GetNewValue<bool>())
        {
            StopFlight();
            _zoomActive = false;
        }

        base.OnPropertyChanged(change);
    }

    /// <summary>主题、字体或语言变化后重建缓存画刷与文本缓存。绘制前调用，开销只有版本比较。</summary>
    private void EnsureThemeBrushes()
    {
        if (_fontVersion != AppFonts.Version || _localeVersion != AppLocalization.Version)
        {
            _fontVersion = AppFonts.Version;
            _localeVersion = AppLocalization.Version;
            _labelTypeface = new Typeface(AppFonts.Ui);
            _textCache.Clear();
            _ratingTexts.Clear();
        }

        if (_themeVersion == ThemeColors.Version) return;
        _themeVersion = ThemeColors.Version;
        _backgroundBrush = new SolidColorBrush(BackgroundColor);
        _nodeBrush = new SolidColorBrush(NodeColor);
        _starBrush = new SolidColorBrush(ThemeColors.Star);
        _labelBrush = new SolidColorBrush(LabelColor);
        _clusterLabelBrush = new SolidColorBrush(ClusterLabelColor);
        _selectionBrush = new SolidColorBrush(SelectionColor);
        _hoverBrush = new SolidColorBrush(HoverColor);
        _favoriteBrush = new SolidColorBrush(SelectionColor);
        _tooltipBrush = new SolidColorBrush(TooltipTextColor);
        _tooltipBackground = new SolidColorBrush(TooltipBackgroundColor);
        _tooltipBorder = new Pen(new SolidColorBrush(TooltipBorderColor), 1);
        _hoverLabelBorder = new Pen(new SolidColorBrush(HoverColor), 1.5);

        _textCache.Clear();
        _ratingTexts.Clear();
        RebuildClusterStyles();
    }

    public override void Render(DrawingContext context)
    {
        EnsureThemeBrushes();
        using var clip = context.PushClip(
            new RoundedRect(new Rect(Bounds.Size), this.CornerRadius));
        var size = Bounds.Size;
        context.FillRectangle(_backgroundBrush, new Rect(size));
        if (size.Width <= 0 || size.Height <= 0) return;

        DrawStars(context, size);

        if (_treeDirty) RebuildNodes();

        var nodes = Nodes;
        if (_needsFit)
        {
            _needsFit = false;
            if (nodes is { Count: > 0 })
                Camera.Fit(GetLayoutBounds(nodes), size, 72);
        }

        if (nodes is null || nodes.Count == 0) return;

        if (Camera.Zoom < OverviewZoomThreshold)
            DrawClusterLayer(context, size);
        else
            DrawNodeLayer(context, size);

        DrawTooltip(context, size);
    }

    // 背景绘制。

    private void DrawStars(DrawingContext context, Size size)
    {
        var offsetX = Camera.CenterX * 0.015;
        var offsetY = Camera.CenterY * 0.015;
        for (var i = 0; i < StarCount; i++)
        {
            var x = Wrap(_starX[i] * size.Width - offsetX, size.Width);
            var y = Wrap(_starY[i] * size.Height - offsetY, size.Height);
            context.DrawEllipse(_starBrush, null, new Point(x, y), _starR[i], _starR[i]);
        }
    }

    private static double Wrap(double value, double length)
    {
        if (length <= 0) return 0;
        var result = value % length;
        return result < 0 ? result + length : result;
    }

    // 簇层绘制，缩放小于 0.35 时启用。

    private void DrawClusterLayer(DrawingContext context, Size size)
    {
        var clusters = Clusters;
        if (clusters is null) return;

        var visible = new List<(GalaxyCluster Cluster, Point Center, double Radius,
            (IBrush Fill, IPen Border, IPen Leader) Style, bool Hovered)>();
        foreach (var cluster in clusters)
        {
            var center = Camera.WorldToScreen(new Point(cluster.CenterX, cluster.CenterY), size);
            var radius = Math.Max(18, cluster.Radius * Camera.Zoom);
            if (center.X + radius < 0 || center.Y + radius < 0 ||
                center.X - radius > size.Width || center.Y - radius > size.Height)
            {
                continue;
            }

            var hovered = _hoverCluster?.Id == cluster.Id;
            var style = hovered && _clusterHoverStyles.TryGetValue(cluster.Id, out var hoverStyle)
                ? hoverStyle
                : _clusterStyles[cluster.Id];

            context.DrawEllipse(style.Fill, style.Border, center, radius, radius);
            visible.Add((cluster, center, radius, style, hovered));
        }

        foreach (var (cluster, center, radius, _, _) in visible)
        {
            if (_clusterSamples.TryGetValue(cluster.Id, out var samples))
                DrawClusterSamples(context, samples, center, radius);
        }

        // 大簇的标签优先占据位置，小簇与之重叠时直接省略；悬停簇的标签最后画、不被省略。
        var placed = new List<Rect>();
        foreach (var item in visible
                     .OrderBy(entry => entry.Hovered ? 1 : 0)
                     .ThenByDescending(entry => entry.Cluster.AssetCount))
        {
            var (cluster, center, radius, style, hovered) = item;
            if (radius < 14) continue;

            var label = GetText(
                $"{ClusterDisplayLabel(cluster)} · {cluster.AssetCount}", 12, _clusterLabelBrush);
            var rect = new Rect(
                center.X - label.Width / 2 - 6,
                center.Y + radius + 4,
                label.Width + 12,
                label.Height + 4);

            if (rect.Bottom > size.Height || rect.Right < 0 || rect.X > size.Width) continue;
            if (!hovered)
            {
                var overlaps = false;
                foreach (var other in placed)
                {
                    if (rect.Intersects(other))
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (overlaps) continue;
            }

            context.DrawLine(
                style.Leader,
                new Point(center.X, center.Y + radius),
                new Point(center.X, rect.Y));

            placed.Add(rect);
            context.DrawRectangle(
                _tooltipBackground, hovered ? _hoverLabelBorder : null, rect, 6, 6);
            context.DrawText(label, new Point(rect.X + 6, rect.Y + 2));
        }
    }

    private void DrawClusterSamples(
        DrawingContext context, List<GalaxyNode> samples, Point center, double radius)
    {
        // 圆太小放不下拼贴时只保留簇名，避免样本图比气泡还大。
        if (radius < 56) return;

        var columns = radius >= 100 && samples.Count > 4 ? 3 : 2;
        var count = Math.Min(columns * columns, samples.Count);
        if (count == 0) return;

        // 拼贴约占圆直径的 60%，大簇用 3×3，小簇用 2×2。
        var gap = 4;
        var thumb = Math.Max(16, (radius * 1.2 - gap * (columns - 1)) / columns);
        var rows = (count + columns - 1) / columns;
        var totalWidth = columns * thumb + (columns - 1) * gap;
        var totalHeight = rows * thumb + (rows - 1) * gap;
        var left = center.X - totalWidth / 2;
        var top = center.Y - totalHeight / 2;

        for (var i = 0; i < count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            var rect = new Rect(
                left + column * (thumb + gap), top + row * (thumb + gap), thumb, thumb);
            var node = samples[i];
            context.DrawRectangle(_nodeBrush, null, rect, 5, 5);

            if (TryGetTexture(node.Asset.Sha256, out var bitmap))
            {
                var source = LightWallLayout.GetCenterCrop(
                    new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height), 1);
                using (context.PushClip(new RoundedRect(rect, 5)))
                {
                    context.DrawImage(bitmap, source, rect);
                }
            }

            RequestLoad(node, 128);
        }
    }

    // 节点层绘制，缩放不小于 0.35 时启用。

    private void DrawNodeLayer(DrawingContext context, Size size)
    {
        var topLeft = Camera.ScreenToWorld(new Point(0, 0), size);
        var bottomRight = Camera.ScreenToWorld(new Point(size.Width, size.Height), size);
        var world = new Rect(
            Math.Min(topLeft.X, bottomRight.X) - NodeHalf,
            Math.Min(topLeft.Y, bottomRight.Y) - NodeHalf,
            Math.Abs(bottomRight.X - topLeft.X) + NodeSize,
            Math.Abs(bottomRight.Y - topLeft.Y) + NodeSize);

        var visible = _quadtree?.Query(world) ?? Nodes ?? Array.Empty<GalaxyNode>();

        using (context.PushTransform(Camera.GetMatrix(size)))
        {
            // 同帧内所有节点共用两支笔，避免悬停标签命中大量节点时逐节点新建。
            var selectionPen = new Pen(_selectionBrush, 2 / Camera.Zoom);
            var hoverPen = _hoverTags.Count > 0
                ? new Pen(_hoverBrush, 1.5 / Camera.Zoom)
                : null;

            foreach (var node in visible)
                DrawNode(context, node, selectionPen, hoverPen);

            DrawClusterLabels(context);
        }
    }

    private void DrawNode(
        DrawingContext context, GalaxyNode node, IPen selectionPen, IPen? hoverPen)
    {
        var dimmed = HighlightedIds is { Count: > 0 } highlighted && !highlighted.Contains(node.Asset.Id);
        if (dimmed)
        {
            using (context.PushOpacity(0.22))
            {
                DrawNodeCore(context, node, selectionPen, hoverPen);
            }
        }
        else
        {
            DrawNodeCore(context, node, selectionPen, hoverPen);
        }
    }

    private void DrawNodeCore(
        DrawingContext context, GalaxyNode node, IPen selectionPen, IPen? hoverPen)
    {
        var rect = new Rect(node.X - NodeHalf, node.Y - NodeHalf, NodeSize, NodeSize);
        context.DrawRectangle(_nodeBrush, null, rect, 10, 10);

        if (TryGetTexture(node.Asset.Sha256, out var bitmap))
        {
            var source = LightWallLayout.GetCenterCrop(
                new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height), 1);
            using (context.PushClip(new RoundedRect(rect, 10)))
            {
                context.DrawImage(bitmap, source, rect);
            }
        }

        if (_selectedNodeId == node.Asset.Id)
        {
            context.DrawRectangle(null, selectionPen, rect.Deflate(1), 9, 9);
        }
        else if (hoverPen is not null && SharesHoverTag(node))
        {
            context.DrawRectangle(null, hoverPen, rect, 10, 10);
        }

        if (Camera.Zoom > 1.2)
        {
            if (node.Asset.IsFavorite)
            {
                context.DrawEllipse(
                    _favoriteBrush,
                    null,
                    new Point(rect.X + 10, rect.Y + 10),
                    4 / Camera.Zoom,
                    4 / Camera.Zoom);
            }

            if (node.Asset.Rating > 0)
            {
                var rating = GetRatingText(
                    node.Asset.Rating, 11 / Camera.Zoom, _tooltipBrush);
                context.DrawText(
                    rating,
                    new Point(rect.Right - rating.Width - 6, rect.Bottom - rating.Height - 4));
            }
        }

        RequestLoad(node, Camera.Zoom > 1.2 ? 256 : 128);
    }

    private void DrawClusterLabels(DrawingContext context)
    {
        var clusters = Clusters;
        if (clusters is null) return;

        var fontSize = AppFonts.GalaxyLabelSize / Camera.Zoom;
        var leader = new Pen(_labelBrush, 1 / Camera.Zoom);
        foreach (var cluster in clusters)
        {
            var text = GetText($"{ClusterDisplayLabel(cluster)} · {cluster.AssetCount}", fontSize, _labelBrush);
            var bottom = cluster.CenterY - cluster.Radius - NodeHalf - 16 / Camera.Zoom;
            context.DrawLine(
                leader,
                new Point(cluster.CenterX, bottom),
                new Point(cluster.CenterX, cluster.CenterY - cluster.Radius));
            context.DrawText(
                text,
                new Point(cluster.CenterX - text.Width / 2, bottom - text.Height));
        }
    }

    private bool SharesHoverTag(GalaxyNode node)
    {
        if (_hoverTags.Count == 0) return false;
        foreach (var tag in node.TagIds)
        {
            if (_hoverTags.Contains(tag)) return true;
        }

        return false;
    }

    // 悬停提示。

    private void DrawTooltip(DrawingContext context, Size size)
    {
        if (_hoverNode is not { } node) return;

        var name = node.Asset.FileName;
        if (string.IsNullOrEmpty(name))
            name = node.Asset.Id.ToString(CultureInfo.InvariantCulture);
        if (name.Length > 42)
            name = name[..40] + "…";

        var clusterName = _clustersById.TryGetValue(node.ClusterId, out var cluster)
            ? ClusterDisplayLabel(cluster)
            : "";
        var text = string.IsNullOrEmpty(clusterName) ? name : $"{name}\n{clusterName}";
        var formatted = GetText(text, 12, _tooltipBrush);

        var x = _pointer.X + 14;
        var y = _pointer.Y + 16;
        if (x + formatted.Width + 20 > size.Width)
            x = Math.Max(4, _pointer.X - formatted.Width - 20);
        if (y + formatted.Height + 16 > size.Height)
            y = Math.Max(4, _pointer.Y - formatted.Height - 20);

        var box = new Rect(x, y, formatted.Width + 16, formatted.Height + 12);
        context.DrawRectangle(_tooltipBackground, _tooltipBorder, box, 6, 6);
        context.DrawText(formatted, new Point(box.X + 8, box.Y + 6));
    }

    private static string ClusterDisplayLabel(GalaxyCluster cluster) =>
        cluster.TagId is null ? AppLocalization.Get("Galaxy.Uncategorized") : cluster.Label;

    // 交互。

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        _pointer = point;
        var left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (left && e.ClickCount == 2)
        {
            // 双击只用于打开图片；簇的单击已经同时负责聚焦与筛选。
            var node = HitNode(point);
            if (node is not null)
            {
                NodeActivated?.Invoke(this, node);
                e.Handled = true;
            }

            _dragging = false;
            _pressedNode = null;
            _pressedCluster = null;
            base.OnPointerPressed(e);
            return;
        }

        if (left)
        {
            StopFlight();
            _zoomActive = false;
            _pressedNode = HitNode(point);
            _pressedCluster = _pressedNode is null ? HitCluster(point) : null;
            _pressPoint = point;
            _dragCenterX = Camera.CenterX;
            _dragCenterY = Camera.CenterY;
            _dragging = true;
            e.Pointer.Capture(this);
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        _pointer = point;

        if (_dragging)
        {
            Camera.CenterX = _dragCenterX - (point.X - _pressPoint.X) / Camera.Zoom;
            Camera.CenterY = _dragCenterY - (point.Y - _pressPoint.Y) / Camera.Zoom;
        }

        UpdateHover(point);
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var point = e.GetPosition(this);
        e.Pointer.Capture(null);

        if (_dragging)
        {
            _dragging = false;
            var moved = Math.Abs(point.X - _pressPoint.X) > 4 ||
                        Math.Abs(point.Y - _pressPoint.Y) > 4;
            if (!moved)
            {
                if (_pressedNode is { } node)
                {
                    SelectNode(node.Asset.Id);
                    NodeSelected?.Invoke(this, node);
                }
                else if (_pressedCluster is { } cluster)
                {
                    FlyTo(cluster);
                    ClusterActivated?.Invoke(this, cluster);
                }
            }
        }

        _pressedNode = null;
        _pressedCluster = null;
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var point = e.GetPosition(this);
        _pointer = point;
        StopFlight();

        // 连续滚动累计目标倍率，由帧回调逐帧靠近；不会每格瞬跳。
        if (!_zoomActive) _targetZoom = Camera.Zoom;
        _zoomAnchor = point;
        _targetZoom = Math.Clamp(
            _targetZoom * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12),
            GalaxyCamera.MinZoom,
            GalaxyCamera.MaxZoom);
        _zoomActive = true;
        RequestFrame();

        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    internal bool IsZooming => _zoomActive;

    /// <summary>推进一帧缩放缓动，围绕指针位置锚定；到位后清除缩放状态。</summary>
    internal bool StepZoom(double dtMs)
    {
        if (!_zoomActive) return false;

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _zoomActive = false;
            return false;
        }

        var diff = _targetZoom - Camera.Zoom;
        if (Math.Abs(diff) < 0.0005)
        {
            ApplyZoom(_targetZoom);
        }
        else
        {
            ApplyZoom(Camera.Zoom + diff * (1 - Math.Exp(-dtMs / 55.0)));
        }

        UpdateHover(_pointer);
        InvalidateVisual();

        var settled = Math.Abs(_targetZoom - Camera.Zoom) < 0.0005;
        if (settled) _zoomActive = false;
        return !settled;
    }

    private void ApplyZoom(double zoom)
    {
        var factor = zoom / Camera.Zoom;
        if (Math.Abs(factor - 1) < 1e-9) return;
        Camera.ZoomAt(_zoomAnchor, factor, Bounds.Size);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointer = new Point(-10000, -10000);
        _hoverNode = null;
        _hoverCluster = null;
        _hoverTags.Clear();
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    private void UpdateHover(Point screen)
    {
        var node = HitNode(screen);
        var cluster = node is null ? HitCluster(screen) : null;
        if (ReferenceEquals(node, _hoverNode) && ReferenceEquals(cluster, _hoverCluster)) return;

        _hoverNode = node;
        _hoverCluster = cluster;
        _hoverTags.Clear();
        if (node is not null)
        {
            foreach (var tag in node.TagIds)
                _hoverTags.Add(tag);
        }
    }

    private GalaxyNode? HitNode(Point screen)
    {
        if (Camera.Zoom < OverviewZoomThreshold) return null;
        if (_treeDirty) RebuildNodes();

        var world = Camera.ScreenToWorld(screen, Bounds.Size);
        return _quadtree?.HitTest(world, NodeHitRadius);
    }

    private GalaxyCluster? HitCluster(Point screen)
    {
        if (Camera.Zoom >= OverviewZoomThreshold) return null;

        var clusters = Clusters;
        if (clusters is null) return null;

        GalaxyCluster? best = null;
        var bestDistance = double.MaxValue;
        foreach (var cluster in clusters)
        {
            var center = Camera.WorldToScreen(
                new Point(cluster.CenterX, cluster.CenterY), Bounds.Size);
            var dx = screen.X - center.X;
            var dy = screen.Y - center.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var radius = Math.Max(30, cluster.Radius * Camera.Zoom) + 8;
            if (distance <= radius && distance < bestDistance)
            {
                bestDistance = distance;
                best = cluster;
            }
        }

        return best;
    }

    private void FlyTo(GalaxyCluster cluster)
    {
        _zoomActive = false;
        _flyFromX = Camera.CenterX;
        _flyFromY = Camera.CenterY;
        _flyFromZoom = Camera.Zoom;
        _flyToX = cluster.CenterX;
        _flyToY = cluster.CenterY;

        // 放大到刚好看清单张图片。小簇可以推近，大簇保持缩放到节点层即可。
        var fit = Math.Min(Bounds.Width, Bounds.Height) / Math.Max(1, cluster.Radius * 2);
        _flyToZoom = Math.Clamp(fit * 0.92, OverviewZoomThreshold + 0.05, 1.3);
        _flying = true;
        _flightClock.Restart();
        RequestFrame();
        InvalidateVisual();
    }

    private void StopFlight()
    {
        _flying = false;
    }

    private bool StepFlight()
    {
        if (!_flying) return false;

        var t = Math.Clamp(_flightClock.Elapsed.TotalMilliseconds / FlightDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        Camera.CenterX = _flyFromX + (_flyToX - _flyFromX) * eased;
        Camera.CenterY = _flyFromY + (_flyToY - _flyFromY) * eased;
        Camera.Zoom = _flyFromZoom + (_flyToZoom - _flyFromZoom) * eased;
        InvalidateVisual();

        if (t >= 1) _flying = false;
        return _flying;
    }

    /// <summary>预约合成器的下一帧；飞行与缩放都按刷新节奏推进。</summary>
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
            StopFlight();
            _zoomActive = false;
            _lastFrameMs = -1;
            return;
        }

        var now = time.TotalMilliseconds;
        var dt = _lastFrameMs < 0 ? 16.0 : Math.Clamp(now - _lastFrameMs, 0.5, 100);
        _lastFrameMs = now;

        var zooming = _zoomActive && StepZoom(dt);
        var flying = StepFlight();

        if (zooming || flying) RequestFrame();
        else _lastFrameMs = -1;
    }

    // 索引与属性变化。

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 节点重排会触发重新适配，正在跑的缩放缓动不能和新视图抢相机。
        _zoomActive = false;
        StopFlight();
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearTextures();
            _hoverNode = null;
            _hoverCluster = null;
            _hoverTags.Clear();
        }

        _treeDirty = true;
        _needsFit = true;
        InvalidateVisual();
    }

    private void OnClustersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildClusterMap();

    private void RebuildNodes()
    {
        _treeDirty = false;
        var nodes = Nodes;
        _quadtree = nodes is null || nodes.Count == 0
            ? null
            : GalaxyQuadtree.Build(nodes);

        // 每簇代表图按收藏、评分与拍摄时间排序，最多取 9 张用于概览拼贴。
        _clusterSamples.Clear();
        if (nodes is null) return;

        foreach (var group in nodes.GroupBy(n => n.ClusterId))
        {
            _clusterSamples[group.Key] = group
                .OrderByDescending(n => n.Asset.IsFavorite)
                .ThenByDescending(n => n.Asset.Rating)
                .ThenByDescending(n => n.Asset.TakenAt)
                .ThenBy(n => n.Asset.Id)
                .Take(9)
                .ToList();
        }
    }

    private void RebuildClusterMap()
    {
        _clustersById.Clear();
        if (Clusters is not null)
        {
            foreach (var cluster in Clusters)
                _clustersById[cluster.Id] = cluster;
        }

        RebuildClusterStyles();
        InvalidateVisual();
    }

    private void RebuildClusterStyles()
    {
        _clusterStyles.Clear();
        _clusterHoverStyles.Clear();
        var clusters = Clusters;
        if (clusters is null) return;

        foreach (var cluster in clusters)
        {
            var color = ParseColor(cluster.Color) ?? DefaultClusterColor;
            var border = new Pen(new SolidColorBrush(WithAlpha(color, 160)), 1.5);
            if (cluster.TagId is null)
            {
                // 未归类用虚线包络，和其他标签簇区分开。
                border = new Pen(new SolidColorBrush(WithAlpha(color, 170)), 1.5)
                {
                    DashStyle = DashStyle.Dash
                };
            }

            _clusterStyles[cluster.Id] = (
                new SolidColorBrush(WithAlpha(color, 30)),
                border,
                new Pen(new SolidColorBrush(WithAlpha(color, 90)), 1));

            _clusterHoverStyles[cluster.Id] = (
                new SolidColorBrush(WithAlpha(color, 70)),
                new Pen(_hoverBrush, 2.5),
                new Pen(_hoverBrush, 1.2));
        }
    }

    /// <summary>节点包围盒再并上簇包络，避免概览层的圆和圆下方标签被裁掉。</summary>
    private Rect GetLayoutBounds(IReadOnlyList<GalaxyNode> nodes)
    {
        var bounds = GetNodeBounds(nodes);
        if (Clusters is { Count: > 0 } clusters)
        {
            foreach (var cluster in clusters)
            {
                var circle = new Rect(
                    cluster.CenterX - cluster.Radius,
                    cluster.CenterY - cluster.Radius,
                    cluster.Radius * 2,
                    cluster.Radius * 2);
                bounds = bounds.Union(circle);
            }
        }

        return bounds;
    }

    private static Rect GetNodeBounds(IReadOnlyList<GalaxyNode> nodes)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var node in nodes)
        {
            minX = Math.Min(minX, node.X - NodeHalf);
            minY = Math.Min(minY, node.Y - NodeHalf);
            maxX = Math.Max(maxX, node.X + NodeHalf);
            maxY = Math.Max(maxY, node.Y + NodeHalf);
        }

        return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    // 纹理缓存。

    private void RequestLoad(GalaxyNode node, int size)
    {
        var provider = ThumbnailProvider;
        if (provider is null) return;

        var key = node.Asset.Sha256;
        if (string.IsNullOrEmpty(key) ||
            _textures.ContainsKey(key) ||
            _loading.Contains(key) ||
            _failed.Contains(key))
        {
            return;
        }

        _loading.Add(key);
        _ = LoadTextureAsync(node.Asset, key, size, provider);
    }

    private async Task LoadTextureAsync(
        AssetListItem item, string key, int size, IThumbnailProvider provider)
    {
        var acquired = false;
        try
        {
            await _loadGate.WaitAsync();
            acquired = true;
            var bitmap = await provider.GetAsync(item, size);
            if (bitmap is not null)
                StoreTexture(key, bitmap);
            else
                _failed.Add(key);
        }
        catch
        {
            _failed.Add(key);
        }
        finally
        {
            if (acquired) _loadGate.Release();
            _loading.Remove(key);
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    private bool TryGetTexture(string key, out Bitmap bitmap)
    {
        if (!string.IsNullOrEmpty(key) && _textures.TryGetValue(key, out var found))
        {
            TouchTexture(key);
            bitmap = found;
            return true;
        }

        bitmap = null!;
        return false;
    }

    private void StoreTexture(string key, Bitmap bitmap)
    {
        _textures[key] = bitmap;
        if (_lruIndex.TryGetValue(key, out var existing))
            _lru.Remove(existing);
        _lruIndex[key] = _lru.AddLast(key);

        while (_textures.Count > MaxTextures && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            _lruIndex.Remove(oldest.Value);
            // 只淘汰引用。Bitmap 由共享的 ThumbnailProvider 缓存，不能 Dispose。
            _textures.Remove(oldest.Value);
        }
    }

    private void TouchTexture(string key)
    {
        if (!_lruIndex.TryGetValue(key, out var node)) return;
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void ClearTextures()
    {
        _textures.Clear();
        _lru.Clear();
        _lruIndex.Clear();
        _loading.Clear();
        _failed.Clear();
    }

    // 绘制工具。

    private FormattedText CreateText(string text, double fontSize, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _labelTypeface, fontSize, brush);

    /// <summary>
    /// 标签文本按「文字 + 半像素字号 + 画刷」缓存；缩放时字号连续变化，缓存设上限。
    /// </summary>
    private FormattedText GetText(string text, double fontSize, IBrush brush)
    {
        var key = (text, (int)Math.Round(fontSize * 2), brush);
        if (_textCache.TryGetValue(key, out var cached)) return cached;

        if (_textCache.Count > 256) _textCache.Clear();
        var created = CreateText(text, key.Item2 / 2.0, brush);
        _textCache[key] = created;
        return created;
    }

    private FormattedText GetRatingText(int rating, double fontSize, IBrush brush)
    {
        var key = (rating, (int)Math.Round(fontSize * 2));
        if (_ratingTexts.TryGetValue(key, out var cached)) return cached;

        if (_ratingTexts.Count > 64) _ratingTexts.Clear();
        var created = CreateText($"★{rating}", key.Item2 / 2.0, brush);
        _ratingTexts[key] = created;
        return created;
    }

    private static Color? ParseColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var color) ? color : null;

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
