using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClipNest.App.Services;
using ClipNest.App.ViewModels;

namespace ClipNest.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>打开设置页时把焦点移进页内，避免 Esc 被被遮住的搜索框吞掉。</summary>
    public void FocusInitial() => BackButton.Focus();

    /// <summary>页头拖动沿用主窗口规则。按钮上的点击不参与，双击切换最大化。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        if (e.ClickCount == 2)
        {
            WindowChrome.ToggleMaximize(window);
            e.Handled = true;
            return;
        }

        WindowChrome.BeginWindowDrag(window, e);
    }

    private void OpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = (DataContext as MainWindowViewModel)?.DataDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            if (DataContext is MainWindowViewModel vm) vm.StatusText = "无法打开数据目录";
        }
    }

    private async void ReidentifyTags_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirmed = await ConfirmWindow.AskAsync(
            owner,
            "重新识别标签",
            "将清除现有 AI 标签，并按各文件夹的模型重新识别全部图片。",
            "角色与作品会按模型结论重建，手动添加的其他标签保留。识别在后台进行，期间可以继续浏览图片。",
            "开始识别");
        if (!confirmed) return;

        try
        {
            await vm.ReidentifyTagsAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = $"重新识别失败：{ex.Message}";
        }
    }

    private async void DownloadTagModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TagModelOption option }) return;
        if (DataContext is not MainWindowViewModel vm) return;
        await vm.DownloadModelAsync(option.Id);
    }

    private async void SetDefaultTagModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TagModelOption option }) return;
        if (DataContext is not MainWindowViewModel vm) return;
        await vm.SetDefaultTagModelAsync(option.Id);
    }

    private void CancelTagModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TagModelOption option }) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.CancelTagModelDownload(option.Id);
    }

    private async void UninstallTagModel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TagModelOption option }) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirmed = await ConfirmWindow.AskAsync(
            owner,
            "卸载识别模型",
            $"将删除本地的 {option.DisplayName} 模型文件。",
            "该模型负责的图片会暂停识别，重新下载后可继续。",
            "卸载");
        if (confirmed) await vm.UninstallTagModelAsync(option.Id);
    }

    private void CancelSemanticModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.CancelSemanticModelDownload();
    }

    private async void UninstallSemanticModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirmed = await ConfirmWindow.AskAsync(
            owner,
            "卸载语义模型",
            "将删除本地的语义模型文件。",
            "语义搜索与索引会暂停，重新下载后可继续。",
            "卸载");
        if (confirmed) await vm.UninstallSemanticModelAsync();
    }

    private async void FolderModel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: FolderTagModelOption option } combo) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var selected = combo.SelectedItem as TagModelOption ?? option.Selected;
        if (string.Equals(selected.Id, option.Folder.TagModelId, StringComparison.Ordinal)) return;
        await vm.SetFolderModelAsync(option.Folder, selected.Id);
    }
}
