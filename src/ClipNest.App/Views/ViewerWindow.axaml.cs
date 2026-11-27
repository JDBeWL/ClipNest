using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipNest.App.Services;
using ClipNest.Core.Models;

namespace ClipNest.App.Views;

public partial class ViewerWindow : Window
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.25;
    private const double StagePadding = 16;
    private const double WheelZoomStep = 1.15;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);

    private IReadOnlyList<AssetListItem> _items;
    private readonly IThumbnailProvider _thumbnails;
    private readonly AssetFileOperations? _operations;
    private AnimatedImagePlayer? _animation;
    private CancellationTokenSource? _prefetch;
    private DispatcherTimer? _statusTimer;
    private int _index;
    private int _loadToken;
    private double _imageWidth;
    private double _imageHeight;
    private double _zoom = 1;
    private bool _fitMode;
    private double _fitMax = 1.0;
    private bool _panning;
    private Point _panStart;
    private Vector _panStartOffset;

    /// <summary>删除成功后通知主窗口重查集合，查看器本身只维护自己的列表。</summary>
    public event EventHandler? LibraryChanged;

    public ViewerWindow(
        IReadOnlyList<AssetListItem> items,
        int index,
        IThumbnailProvider thumbnails,
        AssetFileOperations? operations = null)
    {
        _items = items.Count > 0 ? items : Array.Empty<AssetListItem>();
        _index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        _thumbnails = thumbnails;
        _operations = operations;

        InitializeComponent();
        if (_items.Count > 0) Title = _items[_index].FileName;

        // 没有仓储时隐藏删除，复制与定位仍然可用。
        DeleteButton.IsVisible = operations is not null;
        Stage.ContextMenu = BuildContextMenu();

        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) SyncWindowState();
        };

        Opened += async (_, _) =>
        {
            WindowChrome.ApplyTitleBar(this);
            await ShowCurrentAsync();
        };
        Closed += (_, _) =>
        {
            // 让仍在解码的帧请求失效，避免窗口关闭后才挂上播放器。
            _loadToken++;
            _prefetch?.Cancel();
            StopAnimation();
            _statusTimer?.Stop();
        };

        // 内边距由代码统一设置，适配计算用的是同一个常量，避免两处各写一份后漂移。
        ImageScroll.Padding = new Thickness(StagePadding);

        AddHandler(KeyDownEvent, OnViewerKeyDown, RoutingStrategies.Tunnel);

        // 滚轮缩放走隧道阶段，先于 ScrollViewer 自带的滚动处理。
        ImageScroll.AddHandler(
            PointerWheelChangedEvent, ImageScroll_PointerWheelChanged, RoutingStrategies.Tunnel);

        // 窗口尺寸变化时，未手动缩放过的图片继续保持在适应窗口的状态。
        ImageScroll.SizeChanged += (_, _) =>
        {
            if (_fitMode) ApplyFit();
        };
    }

    /// <summary>
    /// 换一批图片并跳到指定位置。窗口已打开时由主窗口调用，避免每次双击都新开窗口。
    /// </summary>
    public void ShowItem(IReadOnlyList<AssetListItem> items, int index)
    {
        _items = items.Count > 0 ? items : Array.Empty<AssetListItem>();
        _index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = ShowCurrentAsync();
    }

    /// <summary>语言切换后刷新查看器的右键菜单、元信息与窗框提示。</summary>
    public void RefreshLocalizedUi()
    {
        Stage.ContextMenu = BuildContextMenu();
        SyncWindowState();
        if (_items.Count > 0)
        {
            UpdateMetaText(_items[_index]);
        }
    }

    private void UpdateMetaText(AssetListItem item)
    {
        var dimensions = item is { Width: > 0, Height: > 0 }
            ? $"{item.Width} × {item.Height}"
            : AppLocalization.Get("Inspector.UnknownDimensions");
        var date = item.TakenAt?.ToString("yyyy-MM-dd")
            ?? AppLocalization.Get("Inspector.UnknownDate");
        MetaText.Text = $"{dimensions}  ·  {date}";
    }

    private async Task ShowCurrentAsync()
    {
        if (_items.Count == 0) return;
        _statusTimer?.Stop();

        var item = _items[_index];
        var token = ++_loadToken;
        StopAnimation();

        Title = item.FileName;
        FileNameText.Text = item.FileName;
        UpdateMetaText(item);
        InfoText.Text = item.PrimaryPath;
        ToolTip.SetTip(InfoText, item.PrimaryPath);
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;

        _zoom = 1;
        _fitMode = true;
        _fitMax = 1.0;

        // 图库里刚双击的那张图一定已经解码过，先铺上去再换目标尺寸。
        // 这样打开即见画面，不需要遮一层「正在准备」的提示。
        var placeholder = _thumbnails.GetCached(item.Sha256);
        if (placeholder is not null)
        {
            SetImage(placeholder, keepVisualScale: false);
            // 铺底图的像素尺寸不是最终尺寸，先不显示会跳变的百分比。
            ZoomText.Text = "…";
        }
        else
        {
            ImageView.Source = null;
            ImageView.Width = 0;
            ImageView.Height = 0;
            _imageWidth = 0;
            _imageHeight = 0;
            ZoomText.Text = "—";
        }

        var bitmap = await LoadPreviewAsync(item);
        if (token != _loadToken) return;

        if (bitmap is not null)
        {
            SetImage(bitmap, keepVisualScale: true);
            await StartAnimationAsync(item, token);
            SchedulePrefetch(token);
        }
        else if (placeholder is null)
        {
            ZoomText.Text = "—";
            InfoText.Text = AppLocalization.Format("Viewer.PreviewUnavailable", item.PrimaryPath);
        }
    }

    /// <summary>解码失败只影响清晰度，不应使整个查看器崩溃。</summary>
    private async Task<Bitmap?> LoadPreviewAsync(AssetListItem item)
    {
        try
        {
            return await _thumbnails.GetAsync(item, PreviewSize());
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 换上一张位图。从缩略图换成目标尺寸时按视觉尺寸换算缩放，画面不会因像素变多而突然放大；
    /// 用户已手动缩放过时保持原样。
    /// </summary>
    private void SetImage(Bitmap bitmap, bool keepVisualScale)
    {
        var previousWidth = _imageWidth;
        var previousZoom = _zoom;
        var wasFit = _fitMode;

        ImageView.Source = bitmap;
        _imageWidth = bitmap.PixelSize.Width;
        _imageHeight = bitmap.PixelSize.Height;

        if (keepVisualScale && !wasFit && previousWidth > 0)
        {
            _zoom = Math.Clamp(previousZoom * previousWidth / _imageWidth, MinZoom, MaxZoom);
            ApplyZoom(center: false);
            return;
        }

        // 铺底的缩略图允许放大到舞台尺寸，避免小图跳到全尺寸的过渡；
        // 目标尺寸就位后回到「不放大超过原像素」的常规规则。
        _fitMax = keepVisualScale ? 1.0 : MaxZoom;
        _fitMode = true;
        ApplyFit();
    }

    /// <summary>
    /// 停手后预取前后各一张，翻页时直接命中缓存。快速连续翻页会取消上一次预取，
    /// 避免为没有停留的图片生成缓存。
    /// </summary>
    private void SchedulePrefetch(int token)
    {
        _prefetch?.Cancel();
        _prefetch?.Dispose();
        var cts = new CancellationTokenSource();
        _prefetch = cts;
        _ = PrefetchAsync(token, cts.Token);
    }

    private async Task PrefetchAsync(int token, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var index in new[] { _index + 1, _index - 1 })
        {
            if (ct.IsCancellationRequested || token != _loadToken) return;
            if (index < 0 || index >= _items.Count) continue;

            try
            {
                // 不把预取的取消令牌传进解码。同一张图可能正好被用户打开，
                // 共用任务被取消会让正式加载也一起失败。
                await _thumbnails.GetAsync(_items[index], PreviewSize(), CancellationToken.None);
            }
            catch
            {
                // 预取失败不影响当前查看，真正切过去时会重新请求。
            }
        }
    }

    /// <summary>GIF 额外加载逐帧动画；解码期间先显示静态首帧。</summary>
    private async Task StartAnimationAsync(AssetListItem item, int token)
    {
        if (!Path.GetExtension(item.PrimaryPath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            return;

        var player = await AnimatedImagePlayer.LoadAsync(item.PrimaryPath, 1536);
        if (token != _loadToken || player is null || !player.IsAnimated)
        {
            player?.Dispose();
            return;
        }

        _animation = player;
        player.Play(ImageView);
    }

    private void StopAnimation()
    {
        _animation?.Dispose();
        _animation = null;
    }

    /// <summary>
    /// 预览按舞台的物理像素取图。图片以适应窗口的方式整张显示，长边覆盖舞台即可清晰；
    /// 长图不按宽高比额外放大，否则竖图会生成上万像素高的位图。
    /// </summary>
    private int PreviewSize()
    {
        var stage = ImageScroll.Bounds;
        var width = stage.Width > 0 ? stage.Width : ClientSize.Width;
        var height = stage.Height > 0 ? stage.Height : ClientSize.Height;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return ThumbnailSizing.Bucket(Math.Max(width, height) * scaling, 2048);
    }

    /// <summary>按窗口大小计算适应比例；100% 对应图片原始像素，不放大小图。</summary>
    private void ApplyFit()
    {
        if (_imageWidth <= 0 || _imageHeight <= 0) return;

        var bounds = ImageScroll.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var availableWidth = Math.Max(1, bounds.Width - StagePadding * 2);
        var availableHeight = Math.Max(1, bounds.Height - StagePadding * 2);
        var fit = Math.Min(availableWidth / _imageWidth, availableHeight / _imageHeight);
        _zoom = Math.Clamp(fit, MinZoom, _fitMax);
        _fitMode = true;
        ApplyZoom(center: true);
    }

    private void ApplyZoom(bool center)
    {
        ImageView.Width = Math.Max(1, _imageWidth * _zoom);
        ImageView.Height = Math.Max(1, _imageHeight * _zoom);
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        ImageScroll.Cursor = _fitMode ? null : HandCursor;

        if (center)
        {
            // 尺寸与偏移必须同帧就位。先强制布局再改偏移，否则合成器会先渲染
            // 一帧「新尺寸 + 旧偏移」，缩放会出现一次抖动。
            ImageScroll.UpdateLayout();
            CenterImage();
        }
    }

    private void CenterImage()
    {
        var extent = ImageScroll.Extent;
        var viewport = ImageScroll.Viewport;
        ImageScroll.Offset = new Vector(
            Math.Max(0, (extent.Width - viewport.Width) / 2),
            Math.Max(0, (extent.Height - viewport.Height) / 2));
    }

    private void SetZoom(double zoom, bool center)
    {
        if (_imageWidth <= 0) return;

        var target = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(target - _zoom) < 0.0001)
        {
            if (center) ApplyZoom(center: true);
            return;
        }

        _fitMode = false;
        _zoom = target;
        ApplyZoom(center);
    }

    /// <summary>以视口中心为锚点缩放，避免放大后画面发生偏移。</summary>
    private void ZoomBy(double factor)
    {
        if (_imageWidth <= 0) return;

        var old = _zoom;
        var target = Math.Clamp(old * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - old) < 0.0001) return;

        var viewport = ImageScroll.Viewport;
        var center = new Vector(viewport.Width / 2, viewport.Height / 2);
        var anchor = ImageScroll.Offset + center;

        _fitMode = false;
        _zoom = target;
        ApplyZoom(center: false);

        var desired = anchor * (target / old) - center;
        ImageScroll.UpdateLayout();
        SetOffset(desired);
    }

    /// <summary>滚轮以指针位置为锚点缩放。</summary>
    private void ZoomAt(Point point, double factor)
    {
        if (_imageWidth <= 0) return;

        var old = _zoom;
        var target = Math.Clamp(old * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - old) < 0.0001) return;

        var anchor = ImageScroll.Offset + new Vector(point.X, point.Y);

        _fitMode = false;
        _zoom = target;
        ApplyZoom(center: false);

        var desired = anchor * (target / old) - new Vector(point.X, point.Y);
        ImageScroll.UpdateLayout();
        SetOffset(desired);
    }

    private void SetOffset(Vector desired)
    {
        var extent = ImageScroll.Extent;
        var viewport = ImageScroll.Viewport;
        var maxX = Math.Max(0, extent.Width - viewport.Width);
        var maxY = Math.Max(0, extent.Height - viewport.Height);
        ImageScroll.Offset = new Vector(
            Math.Clamp(desired.X, 0, maxX),
            Math.Clamp(desired.Y, 0, maxY));
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => ZoomBy(ZoomStep);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => ZoomBy(1 / ZoomStep);

    private void ZoomReset_Click(object? sender, RoutedEventArgs e)
        => SetZoom(1.0, center: true);

    private void ImageScroll_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_imageWidth <= 0 || Math.Abs(e.Delta.Y) < 0.01) return;

        ZoomAt(e.GetPosition(ImageScroll), e.Delta.Y > 0 ? WheelZoomStep : 1 / WheelZoomStep);
        e.Handled = true;
    }

    private void ImageScroll_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImageScroll).Properties.IsLeftButtonPressed) return;
        if (ImageScroll.Extent.Width <= ImageScroll.Viewport.Width &&
            ImageScroll.Extent.Height <= ImageScroll.Viewport.Height)
        {
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(ImageScroll);
        _panStartOffset = ImageScroll.Offset;
        e.Pointer.Capture(ImageScroll);
        ImageScroll.Cursor = PanCursor;
        e.Handled = true;
    }

    private void ImageScroll_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;

        var delta = e.GetPosition(ImageScroll) - _panStart;
        SetOffset(new Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y));
        e.Handled = true;
    }

    private void ImageScroll_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => EndPan(e.Pointer);

    private void ImageScroll_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _panning = false;
        ImageScroll.Cursor = _fitMode ? null : HandCursor;
    }

    private void EndPan(IPointer pointer)
    {
        if (!_panning) return;

        _panning = false;
        pointer.Capture(null);
        ImageScroll.Cursor = _fitMode ? null : HandCursor;
    }

    private async void Prev_Click(object? sender, RoutedEventArgs e) => await NavigateAsync(-1);
    private async void Next_Click(object? sender, RoutedEventArgs e) => await NavigateAsync(1);

    private async Task NavigateAsync(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _items.Count) return;
        _index = next;
        await ShowCurrentAsync();
    }

    private async void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left:
                await NavigateAsync(-1);
                e.Handled = true;
                break;
            case Key.Right:
                await NavigateAsync(1);
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomBy(ZoomStep);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomBy(1 / ZoomStep);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                SetZoom(1.0, center: true);
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) CopyCurrentFile();
                else _ = CopyCurrentImageAsync();
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteCurrentAsync();
                e.Handled = true;
                break;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var copyImage = new MenuItem { Header = AppLocalization.Get("Menu.CopyImage") };
        copyImage.Click += async (_, _) => await CopyCurrentImageAsync();
        menu.Items.Add(copyImage);

        var copyFile = new MenuItem { Header = AppLocalization.Get("Menu.CopyFile") };
        copyFile.Click += (_, _) => CopyCurrentFile();
        menu.Items.Add(copyFile);

        var copyPath = new MenuItem { Header = AppLocalization.Get("Menu.CopyPath") };
        copyPath.Click += async (_, _) => await CopyCurrentPathAsync();
        menu.Items.Add(copyPath);
        menu.Items.Add(new Separator());

        var reveal = new MenuItem { Header = AppLocalization.Get("Menu.Reveal") };
        reveal.Click += (_, _) => RevealCurrent();
        menu.Items.Add(reveal);

        var delete = new MenuItem
        {
            Header = AppLocalization.Get("Menu.DeleteImage"),
            IsEnabled = _operations is not null
        };
        delete.Click += (_, _) => _ = DeleteCurrentAsync();
        menu.Items.Add(delete);

        return menu;
    }

    private async Task CopyCurrentImageAsync()
    {
        if (_items.Count == 0) return;

        var item = _items[_index];
        var copied = await WindowsClipboard.CopyImageAsync(
            item.PrimaryPath, ImageView.Source as Bitmap);
        FlashStatus(AppLocalization.Get(copied ? "Status.ImageCopied" : "Status.ImageCopyFailed"));
    }

    private void CopyCurrentFile()
    {
        if (_items.Count == 0) return;

        var copied = WindowsClipboard.CopyFiles(new[] { _items[_index].PrimaryPath });
        FlashStatus(AppLocalization.Get(copied ? "Status.FileCopied" : "Status.FileCopyFailed"));
    }

    private async Task CopyCurrentPathAsync()
    {
        if (_items.Count == 0 || Clipboard is null) return;

        await Clipboard.SetTextAsync(_items[_index].PrimaryPath);
        FlashStatus(AppLocalization.Get("Status.PathCopied"));
    }

    private void RevealCurrent()
    {
        if (_items.Count == 0) return;

        FlashStatus(AppLocalization.Get(ExplorerReveal.Reveal(_items[_index].PrimaryPath)
            ? "Status.RevealedInExplorer"
            : "Status.CannotOpenExplorer"));
    }

    /// <summary>删除当前图片。成功后通知主窗口刷新，并把查看器切到相邻一张。</summary>
    private async Task DeleteCurrentAsync()
    {
        if (_operations is null || _items.Count == 0) return;

        var item = _items[_index];
        var confirmed = await ConfirmWindow.AskAsync(
            this,
            AppLocalization.Get("Confirm.DeleteOne.Title"),
            AppLocalization.Get("Confirm.Delete.Message"),
            item.FileName,
            AppLocalization.Get("Confirm.Delete.Action"));
        if (!confirmed) return;

        BatchOutcome outcome;
        try
        {
            outcome = await _operations.DeleteAsync(new[] { item });
        }
        catch (Exception ex)
        {
            FlashStatus(AppLocalization.Format("Status.DeleteFailed", ex.Message));
            return;
        }

        if (outcome.Succeeded == 0)
        {
            FlashStatus(outcome.Errors.FirstOrDefault() ?? AppLocalization.Get("Viewer.DeleteUnavailable"));
            return;
        }
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        await RemoveCurrentAsync(item);
    }

    private async Task RemoveCurrentAsync(AssetListItem removed)
    {
        var remaining = _items.Where(candidate => candidate.Id != removed.Id).ToList();
        if (remaining.Count == 0)
        {
            Close();
            return;
        }

        _items = remaining;
        _index = Math.Clamp(_index, 0, remaining.Count - 1);
        await ShowCurrentAsync();
    }

    /// <summary>底部路径文字临时显示操作结果，2.4 秒后恢复当前图片的路径。</summary>
    private void FlashStatus(string message)
    {
        InfoText.Text = message;

        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2.4),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _statusTimer?.Stop();
                if (_items.Count > 0) InfoText.Text = _items[_index].PrimaryPath;
            });
        _statusTimer.Start();
    }

    private async void CopyImage_Click(object? sender, RoutedEventArgs e)
        => await CopyCurrentImageAsync();

    private void CopyFile_Click(object? sender, RoutedEventArgs e) => CopyCurrentFile();

    private async void CopyPath_Click(object? sender, RoutedEventArgs e)
        => await CopyCurrentPathAsync();

    private void Reveal_Click(object? sender, RoutedEventArgs e) => RevealCurrent();

    private void Delete_Click(object? sender, RoutedEventArgs e) => _ = DeleteCurrentAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        if (e.ClickCount == 2)
        {
            WindowChrome.ToggleMaximize(this);
            e.Handled = true;
            return;
        }

        WindowChrome.BeginWindowDrag(this, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e)
        => WindowChrome.ToggleMaximize(this);

    private void SyncWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        ToolTip.SetTip(
            MaxRestoreButton,
            AppLocalization.Get(maximized ? "Common.Restore" : "Common.Maximize"));
        RootLayout.Margin = WindowChrome.GetMaximizedInset(this);
        if (WindowState == WindowState.Normal) WindowChrome.EnsureVisible(this);
    }
}
