using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClipNest.App.Views;

public partial class ErrorWindow : Window
{
    private readonly string _dataRoot;

    public ErrorWindow(string message, string dataRoot)
    {
        InitializeComponent();
        MessageText.Text = message;
        PathText.Text = dataRoot;
        _dataRoot = dataRoot;
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var target = Directory.Exists(_dataRoot)
                ? _dataRoot
                : Path.GetDirectoryName(_dataRoot);
            if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        }
        catch
        {
            // 打不开资源管理器时不影响用户阅读错误信息。
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
