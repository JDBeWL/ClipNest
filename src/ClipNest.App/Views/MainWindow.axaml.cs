using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipNest.App.Services;
using ClipNest.App.Theme;
using ClipNest.App.ViewModels;
using ClipNest.Core.Models;

namespace ClipNest.App.Views;

public partial class MainWindow : Window
{
    // 搜索框聚焦时的高亮边，跟随当前主题取色，浅色下强调色更饱和。
    private static IBrush AccentBorder => new SolidColorBrush(ThemeColors.Accent);

    private readonly IThumbnailProvider _thumbnails;
    private readonly MainWindowViewModel _vm;
    private readonly AssetFileOperations? _operations;
    private static readonly TimeSpan ViewFadeDuration = TimeSpan.FromMilliseconds(120);
    private readonly Stopwatch _viewFadeClock = new();
    private Control? _viewFadeOutgoing;
    private bool _viewFrameScheduled;
    private ViewerWindow? _viewer;

    public MainWindow(
        MainWindowViewModel vm,
        IThumbnailProvider thumbnails,
        SettingsService settings,
        AssetFileOperations? operations = null)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _thumbnails = thumbnails;
        _operations = operations;

        // Windows 对没有指定位置的新窗口会做层叠摆放，每次启动都往右下偏一截。
        // 这里显式还原上次的位置，首次启动则居中到工作区。
        ApplyWindowPlacement(settings.Settings);

        Gallery.ThumbnailProvider = thumbnails;
        Wall.ThumbnailProvider = thumbnails;
        Galaxy.ThumbnailProvider = thumbnails;

        ApplyCurrentView(animate: false);

        Gallery.SelectionChanged += (_, items) => vm.ApplySelection(items);
        Gallery.ItemActivated += (_, item) => OpenViewer(vm, item);
        Gallery.ContextMenuFactory = BuildAssetContextMenu;
        Gallery.ZoomRequested += (_, delta) =>
            vm.ThumbnailSize = Math.Clamp(vm.ThumbnailSize + delta, 132, 340);

        Wall.TileSelected += (_, item) => vm.ApplySelection(new[] { item });
        Wall.TileActivated += (_, item) => OpenViewer(vm, item);

        Galaxy.NodeSelected += (_, node) => vm.ApplySelection(new[] { node.Asset });
        Galaxy.NodeActivated += (_, node) => OpenViewer(vm, node.Asset);
        Galaxy.ClusterActivated += (_, cluster) => { _ = vm.SearchClusterAsync(cluster); };

        // 三个视图共享同一选中项，ViewModel 变化时同步瀑布流、光墙与星系的高亮。
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentView))
            {
                ApplyCurrentView(animate: true);
                return;
            }

            if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
            {
                // 等 IsVisible 绑定生效后再聚焦，否则焦点仍留在底层隐藏的搜索框中，
                // 会在隧道阶段拦截 Esc。
                if (vm.IsSettingsOpen)
                    Dispatcher.UIThread.Post(() => SettingsPage.FocusInitial(), DispatcherPriority.Loaded);
                else
                    SearchBox.Focus();
                return;
            }

            if (e.PropertyName != nameof(MainWindowViewModel.SelectedItem)) return;
            var id = vm.SelectedItem?.Id;
            Gallery.SyncPrimary(id);
            Wall.SelectItem(id);
            Galaxy.SelectNode(id);
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // TextBox 会在冒泡阶段消费 Enter，通过隧道阶段提前接管搜索与编辑快捷键。
        SearchBox.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);
        CharacterEditBox.AddHandler(KeyDownEvent, CharacterEditBox_KeyDown, RoutingStrategies.Tunnel);
        SeriesEditBox.AddHandler(KeyDownEvent, SeriesEditBox_KeyDown, RoutingStrategies.Tunnel);

        Closing += (_, _) =>
        {
            FinishViewFade();
            CaptureWindowPlacement(settings.Settings);
            vm.SaveSettingsNow();
        };

        // 自绘标题栏，按钮图标跟随窗口状态切换，并让系统窗框保持深色。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) SyncWindowState();
        };
        Opened += (_, _) =>
        {
            WindowChrome.ApplyTitleBar(this);
            // 上次的位置可能落在已经拔掉的显示器上，拉回当前工作区。
            WindowChrome.EnsureVisible(this);
        };

        Opened += async (_, _) =>
        {
            await vm.LoadFoldersAsync();
            await vm.BackfillDimensionsAsync();
            await vm.PruneOrphansAsync();
            await vm.LoadAsync();
            await vm.PruneUnusedTagsAsync();
            await vm.LoadTagsAsync();
            if (vm.IsGalaxy) await vm.LoadGalaxyAsync();
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>
    /// 三个视图叠在同一格子里，切换时新视图直接铺底、旧视图在上层淡出。
    /// 旧视图保留到淡出结束，中间不会露出窗口底色。
    /// </summary>
    private void ApplyCurrentView(bool animate)
    {
        var views = new Control[] { Gallery, Wall, Galaxy };
        var target = _vm.IsGrid ? (Control)Gallery : _vm.IsLightWall ? Wall : Galaxy;

        FinishViewFade();

        if (!animate)
        {
            foreach (var view in views)
            {
                if (view != target) HideView(view);
            }
        }

        var outgoing = animate
            ? views.FirstOrDefault(view => view != target && view.IsVisible)
            : null;

        foreach (var view in views)
        {
            if (view == target || view == outgoing) continue;
            HideView(view);
        }

        target.IsVisible = true;
        target.Opacity = 1;
        target.ZIndex = 0;
        target.IsHitTestVisible = true;

        if (outgoing is null) return;

        outgoing.ZIndex = 1;
        outgoing.IsHitTestVisible = false;
        _viewFadeOutgoing = outgoing;
        _viewFadeClock.Restart();
        RequestViewFrame();
    }

    private static void HideView(Control view)
    {
        view.IsVisible = false;
        view.Opacity = 1;
        view.ZIndex = 0;
        view.IsHitTestVisible = true;
    }

    /// <summary>视图淡出跟随合成器帧推进，高刷屏上不会只按 60fps 更新。</summary>
    private void RequestViewFrame()
    {
        if (_viewFrameScheduled) return;

        _viewFrameScheduled = true;
        RequestAnimationFrame(OnViewFadeFrame);
    }

    private void OnViewFadeFrame(TimeSpan time)
    {
        _viewFrameScheduled = false;
        if (_viewFadeOutgoing is not { } outgoing) return;

        var progress = Math.Clamp(
            _viewFadeClock.Elapsed.TotalMilliseconds / ViewFadeDuration.TotalMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        outgoing.Opacity = 1 - eased;

        if (progress >= 1) FinishViewFade();
        else RequestViewFrame();
    }

    private void FinishViewFade()
    {
        if (_viewFadeOutgoing is not { } outgoing) return;

        HideView(outgoing);
        _viewFadeOutgoing = null;
    }

    /// <summary>
    /// 首次启动居中，之后回到上次关闭时的位置与尺寸。
    /// 尺寸下限取窗口自身的 MinWidth 与 MinHeight，避免恢复出一个放不下界面的窗口。
    /// </summary>
    private void ApplyWindowPlacement(AppSettings settings)
    {
        var width = settings.WindowWidth is > 0 ? settings.WindowWidth.Value : Width;
        var height = settings.WindowHeight is > 0 ? settings.WindowHeight.Value : Height;
        Width = Math.Max(width, MinWidth);
        Height = Math.Max(height, MinHeight);

        if (settings.WindowX is int x && settings.WindowY is int y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            return;
        }

        // 没有记录时交给系统按工作区居中，避免落在层叠摆放的位置上。
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void CaptureWindowPlacement(AppSettings settings)
    {
        // 最大化或最小化时记录的是屏幕尺寸，会破坏下次启动的普通位置。
        if (WindowState != WindowState.Normal) return;

        settings.WindowX = Position.X;
        settings.WindowY = Position.Y;
        settings.WindowWidth = Bounds.Width;
        settings.WindowHeight = Bounds.Height;
    }

    /// <summary>
    /// 标题栏拖动。空白处按下即交给系统移动窗口，双击切换最大化；
    /// 落在窗口按钮上的点击不参与拖动。
    /// </summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source &&
            (source.FindAncestorOfType<Button>(includeSelf: true) is not null ||
             source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        WindowChrome.BeginWindowDrag(this, e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void CancelTagModelDownload_Click(object? sender, RoutedEventArgs e)
        => _vm.CancelTagModelDownload(_vm.DefaultTagModelId);

    private void OpenVcRuntimeDownload_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://aka.ms/vs/17/release/vc_redist.x64.exe")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _vm.StatusText = AppLocalization.Format("Runtime.OpenDownloadFailed", ex.Message);
        }
    }

    /// <summary>语言切换后同步更新窗框按钮的悬停提示。</summary>
    public void RefreshLocalizedChrome() => SyncWindowState();

    private void ToggleMaximize() => WindowChrome.ToggleMaximize(this);

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

    private void OpenViewer(MainWindowViewModel vm, AssetListItem item)
    {
        vm.ApplySelection(new[] { item });

        var items = vm.Items.ToList();
        var index = items.FindIndex(candidate => candidate.Id == item.Id);
        if (index < 0)
        {
            items = new List<AssetListItem> { item };
            index = 0;
        }

        // 独立非模态窗口，拥有独立任务栏按钮并可置于主窗后方。
        // 已经打开时直接切换图片并置顶，避免连续双击打开多个窗口。
        if (_viewer is { } existing)
        {
            existing.ShowItem(items, index);
            return;
        }

        var viewer = new ViewerWindow(items, index, _thumbnails, _operations);
        viewer.Closed += (_, _) => _viewer = null;

        // 查看器里删除的图片需要同步从图库消失，否则画廊仍会画已被回收的缩略图。
        viewer.LibraryChanged += (_, _) => _ = _vm.RefreshLibraryAsync();
        _viewer = viewer;
        viewer.Show();
    }

    private async Task PickAndAddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = AppLocalization.Get("Picker.ImportFolder")
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null && Vm is { } vm)
            await vm.AddFolderAsync(path);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
        => await PickAndAddFolderAsync();

    private async void RefreshFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Folder folder } && Vm is { } vm)
            await vm.RefreshFolderAsync(folder);
    }

    private async void RemoveFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Folder folder } && Vm is { } vm)
            await vm.RemoveFolderAsync(folder);
    }

    private void FolderRow_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: Folder folder } control) return;

        // 与图片右键一致使用 ContextMenu，共用同一套弹层样式。
        var menu = new ContextMenu();

        var reanalyze = new MenuItem { Header = AppLocalization.Get("Menu.ReanalyzeTags") };
        reanalyze.Click += async (_, _) => await ConfirmReanalyzeFolderAsync(folder);
        menu.Items.Add(reanalyze);
        menu.Items.Add(new Separator());

        var modelMenu = new MenuItem { Header = AppLocalization.Get("Menu.TagModel") };
        AddFolderModelItem(modelMenu, folder, "", AppLocalization.Get("Common.FollowDefault"));
        foreach (var option in _vm.TagModels)
        {
            AddFolderModelItem(modelMenu, folder, option.Id, option.DisplayName);
        }

        menu.Items.Add(modelMenu);
        menu.Open(control);
        e.Handled = true;
    }

    private async Task ConfirmReanalyzeAssetAsync(AssetListItem item)
    {
        var confirmed = await ConfirmWindow.AskAsync(
            this,
            AppLocalization.Get("Confirm.Reanalyze.Title"),
            AppLocalization.Format("Confirm.ReanalyzeAsset.Message", item.FileName),
            AppLocalization.Get("Confirm.ReanalyzeAsset.Detail"),
            AppLocalization.Get("Confirm.StartTagging"));
        if (confirmed) await _vm.ReanalyzeAssetAsync(item);
    }

    private async Task ConfirmReanalyzeFolderAsync(Folder folder)
    {
        var confirmed = await ConfirmWindow.AskAsync(
            this,
            AppLocalization.Get("Confirm.Reanalyze.Title"),
            AppLocalization.Format("Confirm.ReanalyzeFolder.Message", folder.Name),
            AppLocalization.Get("Confirm.ReanalyzeBatch.Detail"),
            AppLocalization.Get("Confirm.StartTagging"));
        if (confirmed) await _vm.ReanalyzeFolderAsync(folder);
    }

    private void AddFolderModelItem(
        MenuItem parent, Folder folder, string modelId, string header)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = string.Equals(folder.TagModelId, modelId, StringComparison.Ordinal)
        };
        item.Click += async (_, _) => await _vm.SetFolderModelAsync(folder, modelId);
        parent.Items.Add(item);
    }

    private async void RemoveTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Tag tag } && Vm is { } vm)
            await vm.RemoveTagAsync(tag);
    }

    private async void Tag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TagTreeItemViewModel tag } && Vm is { } vm)
            await vm.FilterByTagAsync(tag.Path);
    }

    private void ResetGalaxy_Click(object? sender, RoutedEventArgs e) => Galaxy.ResetView();

    private void CloseInspector_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsInspectorOpen = false;
    }

    private void ToggleInspector_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsInspectorOpen = !vm.IsInspectorOpen;
    }

    private void ToggleSemantic_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.SemanticEnabled = !vm.SemanticEnabled;
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e) => Gallery.ClearSelection();

    private async void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        Gallery.ClearSelection();
        await vm.ShowAllAsync();
    }

    private void SortDefault_Click(object? sender, RoutedEventArgs e) => Vm?.SetSortMode("default");
    private void SortTaken_Click(object? sender, RoutedEventArgs e) => Vm?.SetSortMode("taken");
    private void SortName_Click(object? sender, RoutedEventArgs e) => Vm?.SetSortMode("name");
    private void SortRating_Click(object? sender, RoutedEventArgs e) => Vm?.SetSortMode("rating");

    private void SearchBox_GotFocus(object? sender, GotFocusEventArgs e)
        => SearchShell.BorderBrush = AccentBorder;

    private void SearchBox_LostFocus(object? sender, RoutedEventArgs e)
        => SearchShell.ClearValue(Border.BorderBrushProperty);

    private void CharacterEditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key is Key.Enter or Key.Return)
        {
            vm.CommitCharactersCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelCharactersCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void SeriesEditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key is Key.Enter or Key.Return)
        {
            vm.CommitSeriesCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelSeriesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key is Key.Enter or Key.Return)
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.ShowAllCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RunSearch_Click(object? sender, RoutedEventArgs e)
        => Vm?.SearchCommand.Execute(null);

    private void OpenSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { SelectedItem: { } item } vm) OpenViewer(vm, item);
    }

    private void RevealSelected_Click(object? sender, RoutedEventArgs e)
    {
        var path = Vm?.SelectedItem?.PrimaryPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!ExplorerReveal.Reveal(path) && Vm is { } vm)
        {
            vm.StatusText = AppLocalization.Get("Status.CannotOpenExplorer");
        }
    }

    private async void CopyPath_Click(object? sender, RoutedEventArgs e)
    {
        var path = Vm?.SelectedItem?.PrimaryPath;
        if (string.IsNullOrWhiteSpace(path) || Clipboard is null) return;

        await Clipboard.SetTextAsync(path);
        if (Vm is { } vm) vm.StatusText = AppLocalization.Get("Status.PathCopied");
    }

    /// <summary>右键落在已选中的图片上时批量处理全部选中项；否则只处理当前图片。</summary>
    private List<AssetListItem> ContextTargets(AssetListItem item)
    {
        var selected = _vm.SelectedItems;
        return selected.Count > 1 && selected.Any(candidate => candidate.Id == item.Id)
            ? selected.ToList()
            : new List<AssetListItem> { item };
    }

    private ContextMenu BuildAssetContextMenu(AssetListItem item)
    {
        var targets = ContextTargets(item);
        var menu = new ContextMenu();

        var open = new MenuItem { Header = AppLocalization.Get("Menu.Open") };
        open.Click += (_, _) => OpenViewer(_vm, item);
        menu.Items.Add(open);

        var reveal = new MenuItem { Header = AppLocalization.Get("Menu.Reveal") };
        reveal.Click += (_, _) => ExplorerReveal.Reveal(item.PrimaryPath);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = AppLocalization.Get("Menu.Copy") };
        var copyImage = new MenuItem { Header = AppLocalization.Get("Menu.CopyImage") };
        copyImage.Click += async (_, _) => await CopyImageAsync(item);
        copy.Items.Add(copyImage);

        var copyFiles = new MenuItem
        {
            Header = targets.Count > 1
                ? AppLocalization.Format("Menu.CopyFilesCount", targets.Count)
                : AppLocalization.Get("Menu.CopyFile")
        };
        copyFiles.Click += (_, _) => CopyFiles(targets);
        copy.Items.Add(copyFiles);

        var copyPath = new MenuItem { Header = AppLocalization.Get("Menu.CopyPath") };
        copyPath.Click += (_, _) => _ = CopyPathsAsync(targets);
        copy.Items.Add(copyPath);
        menu.Items.Add(copy);

        menu.Items.Add(BuildTransferMenu(AppLocalization.Get("Menu.CopyTo"), FileTransferMode.Copy, targets));
        menu.Items.Add(BuildTransferMenu(AppLocalization.Get("Menu.MoveTo"), FileTransferMode.Move, targets));
        menu.Items.Add(new Separator());

        var reanalyze = new MenuItem { Header = AppLocalization.Get("Menu.ReanalyzeTags") };
        reanalyze.Click += async (_, _) => await ConfirmReanalyzeAssetAsync(item);
        menu.Items.Add(reanalyze);
        menu.Items.Add(new Separator());

        var delete = new MenuItem
        {
            Header = targets.Count > 1
                ? AppLocalization.Format("Menu.DeleteImagesCount", targets.Count)
                : AppLocalization.Get("Menu.DeleteImage")
        };
        delete.Click += (_, _) => _ = ConfirmAndDeleteAsync(targets);
        menu.Items.Add(delete);

        return menu;
    }

    private MenuItem BuildTransferMenu(
        string header, FileTransferMode mode, IReadOnlyList<AssetListItem> targets)
    {
        var menu = new MenuItem { Header = header };

        foreach (var folder in _vm.Folders)
        {
            var entry = new MenuItem { Header = folder.Name };
            ToolTip.SetTip(entry, folder.Path);
            entry.Click += async (_, _) => await _vm.TransferAsync(targets, folder.Path, mode);
            menu.Items.Add(entry);
        }

        if (_vm.Folders.Count > 0) menu.Items.Add(new Separator());

        var browse = new MenuItem { Header = AppLocalization.Get("Menu.ChooseOtherFolder") };
        browse.Click += async (_, _) => await PickAndTransferAsync(targets, mode);
        menu.Items.Add(browse);
        return menu;
    }

    private void CopyTo_Click(object? sender, RoutedEventArgs e)
        => ShowTransferFlyout(sender as Button, FileTransferMode.Copy);

    private void MoveTo_Click(object? sender, RoutedEventArgs e)
        => ShowTransferFlyout(sender as Button, FileTransferMode.Move);

    /// <summary>多选操作条的复制到与移动到共用一份文件夹列表，从按钮上方展开。</summary>
    private void ShowTransferFlyout(Button? anchor, FileTransferMode mode)
    {
        var targets = _vm.SelectionSnapshot();
        if (anchor is null || targets.Count == 0) return;

        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            VerticalOffset = -8
        };
        foreach (var folder in _vm.Folders)
        {
            var entry = new MenuItem { Header = folder.Name };
            ToolTip.SetTip(entry, folder.Path);
            entry.Click += async (_, _) => await _vm.TransferAsync(targets, folder.Path, mode);
            flyout.Items.Add(entry);
        }

        if (_vm.Folders.Count > 0) flyout.Items.Add(new Separator());

        var browse = new MenuItem { Header = AppLocalization.Get("Menu.ChooseOtherFolder") };
        browse.Click += async (_, _) => await PickAndTransferAsync(targets, mode);
        flyout.Items.Add(browse);

        flyout.ShowAt(anchor);
    }

    private async Task PickAndTransferAsync(
        IReadOnlyList<AssetListItem> targets, FileTransferMode mode)
    {
        var folder = await PickFolderAsync(AppLocalization.Get(
            mode == FileTransferMode.Move ? "Picker.MoveToFolder" : "Picker.CopyToFolder"));
        if (folder is null) return;

        await _vm.TransferAsync(targets, folder, mode);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = title
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private void DeleteSelection_Click(object? sender, RoutedEventArgs e)
        => _ = ConfirmAndDeleteAsync(_vm.SelectionSnapshot());

    /// <summary>主窗口删除图片的唯一入口。用户确认后文件移入回收站，再调用仓储。</summary>
    private async Task ConfirmAndDeleteAsync(IReadOnlyList<AssetListItem> items)
    {
        if (items.Count == 0) return;

        var detail = string.Join(Environment.NewLine, items.Take(3).Select(item => item.FileName));
        if (items.Count > 3)
            detail += $"{Environment.NewLine}{AppLocalization.Format("Confirm.Delete.MoreItems", items.Count)}";

        var confirmed = await ConfirmWindow.AskAsync(
            this,
            items.Count > 1
                ? AppLocalization.Format("Confirm.DeleteMany.Title", items.Count)
                : AppLocalization.Get("Confirm.DeleteOne.Title"),
            AppLocalization.Get("Confirm.Delete.Message"),
            detail,
            AppLocalization.Get("Confirm.Delete.Action"));
        if (!confirmed) return;

        await _vm.DeleteAssetsAsync(items);
    }

    private async Task CopyImageAsync(AssetListItem item)
    {
        // 解码原文件失败时退回缓存的缩略图，RAW 与 HEIC 也能复制。
        var copied = await WindowsClipboard.CopyImageAsync(
            item.PrimaryPath, _thumbnails.GetCached(item.Sha256));
        _vm.StatusText = AppLocalization.Get(copied ? "Status.ImageCopied" : "Status.ImageCopyFailed");
    }

    private void CopyFiles(IReadOnlyList<AssetListItem> items)
    {
        var paths = items
            .Select(item => item.PrimaryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .ToList();

        if (paths.Count == 0)
        {
            _vm.StatusText = AppLocalization.Get("Status.FilesMissing");
            return;
        }

        _vm.StatusText = WindowsClipboard.CopyFiles(paths)
            ? AppLocalization.FormatCount("Status.FilesCopiedOne", "Status.FilesCopied", paths.Count)
            : AppLocalization.Get("Status.ClipboardFailed");
    }

    private async Task CopyPathsAsync(IReadOnlyList<AssetListItem> items)
    {
        if (Clipboard is null) return;

        var text = string.Join(Environment.NewLine, items.Select(item => item.PrimaryPath));
        if (string.IsNullOrWhiteSpace(text)) return;

        await Clipboard.SetTextAsync(text);
        _vm.StatusText = items.Count > 1
            ? AppLocalization.Format("Status.PathsCopied", items.Count)
            : AppLocalization.Get("Status.PathCopied");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Vm is { } vm)
        {
            // 设置页打开时只处理 Esc，其余全局快捷键让位，避免操作被遮住的控件。
            if (vm.IsSettingsOpen)
            {
                if (e.Key == Key.Escape)
                {
                    vm.CloseSettingsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                base.OnKeyDown(e);
                return;
            }

            if (e.Key == Key.Delete && FocusManager?.GetFocusedElement() is not TextBox)
            {
                var targets = vm.SelectionSnapshot();
                if (targets.Count > 0)
                {
                    e.Handled = true;
                    _ = ConfirmAndDeleteAsync(targets);
                    return;
                }
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                switch (e.Key)
                {
                    case Key.C:
                    {
                        // 输入框里没有选中文本时 Ctrl+C 会冒泡到窗口，此时不应复制图片。
                        if (FocusManager?.GetFocusedElement() is TextBox) return;
                        var targets = vm.SelectionSnapshot();
                        if (targets.Count == 0) return;
                        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                            _ = CopyPathsAsync(targets);
                        else
                            CopyFiles(targets);
                        e.Handled = true;
                        return;
                    }
                    case Key.K:
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                        e.Handled = true;
                        return;
                    case Key.B:
                        vm.IsInspectorOpen = !vm.IsInspectorOpen;
                        e.Handled = true;
                        return;
                    case Key.O:
                        _ = PickAndAddFolderAsync();
                        e.Handled = true;
                        return;
                    case Key.D1:
                        vm.ShowGridCommand.Execute(null);
                        e.Handled = true;
                        return;
                    case Key.D2:
                        vm.ShowWallCommand.Execute(null);
                        e.Handled = true;
                        return;
                    case Key.D3:
                        vm.ShowGalaxyCommand.Execute(null);
                        e.Handled = true;
                        return;
                    case Key.OemComma:
                        vm.OpenSettingsCommand.Execute(null);
                        e.Handled = true;
                        return;
                }
            }
        }

        base.OnKeyDown(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm?.IsSettingsOpen == true) return;
        if (Vm is not { } vm) return;

        var paths = e.DataTransfer?.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => path!)
            .ToList();

        if (paths is null || paths.Count == 0) return;

        foreach (var path in paths)
            await vm.AddFolderAsync(path);
    }
}
