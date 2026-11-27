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

    /// <summary>打开设置页时把焦点移入页内，避免 Esc 被底层隐藏的搜索框拦截。</summary>
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
            if (DataContext is MainWindowViewModel vm)
                vm.StatusText = AppLocalization.Get("Status.CannotOpenDataDir");
        }
    }

    private async void ReidentifyTags_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirmed = await ConfirmWindow.AskAsync(
            owner,
            AppLocalization.Get("Confirm.ReanalyzeAll.Title"),
            AppLocalization.Get("Confirm.ReanalyzeAll.Message"),
            AppLocalization.Get("Confirm.ReanalyzeBatch.Detail"),
            AppLocalization.Get("Confirm.StartTagging"));
        if (!confirmed) return;

        try
        {
            await vm.ReidentifyTagsAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = AppLocalization.Format("Ai.Tagging.ReidentifyFailed", ex.Message);
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
            AppLocalization.Get("Confirm.UninstallTagModel.Title"),
            AppLocalization.Format("Confirm.UninstallTagModel.Message", option.DisplayName),
            AppLocalization.Get("Confirm.UninstallTagModel.Detail"),
            AppLocalization.Get("Common.Uninstall"));
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
            AppLocalization.Get("Confirm.UninstallSemantic.Title"),
            AppLocalization.Get("Confirm.UninstallSemantic.Message"),
            AppLocalization.Get("Confirm.UninstallSemantic.Detail"),
            AppLocalization.Get("Common.Uninstall"));
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
