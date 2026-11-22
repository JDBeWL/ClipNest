using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipNest.App.Services;

namespace ClipNest.App.Views;

/// <summary>
/// 删除等不可逆操作的确认窗口。确认按钮永不设为默认，
/// 回车与 Esc 都落在取消上，避免误触后直接丢失文件。
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string detail, string confirmText)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        DetailText.Text = detail;
        DetailShell.IsVisible = !string.IsNullOrWhiteSpace(detail);

        Opened += (_, _) =>
        {
            WindowChrome.ApplyTitleBar(this);
            Dispatcher.UIThread.Post(() => CancelButton.Focus(), DispatcherPriority.Loaded);
        };
    }

    /// <summary>以模态方式询问用户，返回是否点了确认按钮。</summary>
    public static Task<bool> AskAsync(
        Window owner, string title, string message, string detail, string confirmText)
    {
        var dialog = new ConfirmWindow(title, message, detail, confirmText);
        return dialog.ShowDialog<bool>(owner);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void CloseDialog_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 落在关闭按钮上的点击不参与拖动，否则按钮会变成窗口拖拽的起点。
        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        WindowChrome.BeginWindowDrag(this, e);
    }
}

