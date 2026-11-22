using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipNest.App.Theme;

namespace ClipNest.App.Tests;

public sealed class DangerButtonStyleTests
{
    [AvaloniaFact]
    public void Hover_PaintsDangerInsideButtonTemplate()
    {
        var button = new Button
        {
            Classes = { "icon", "danger" },
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        // 色板按 Dark/Light 两套字典存放，headless 默认变体没有对应条目，动态资源会落空。
        var window = new Window
        {
            Width = 80,
            Height = 80,
            Content = button,
            RequestedThemeVariant = ThemeVariant.Dark
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 通用 danger 样式一旦泄漏到图标按钮，Padding 会把 12px 图标挤偏。
        Assert.Equal(new Thickness(0), button.Padding);

        window.MouseMove(new Point(6, 6));
        Dispatcher.UIThread.RunJobs();

        var presenters = button.GetVisualDescendants().OfType<ContentPresenter>().ToList();
        Assert.True(button.IsPointerOver, $"指针未进入按钮，presenters={presenters.Count}");
        var presenter = presenters.First();
        var background = Assert.IsType<SolidColorBrush>(presenter.Background);
        Assert.Equal(ThemeColors.Danger, background.Color);

        window.Close();
    }
}
