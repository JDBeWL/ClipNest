using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ClipNest.App.Views;

namespace ClipNest.App.Tests;

public sealed class ConfirmWindowTests
{
    [AvaloniaFact]
    public void LoadsTitleAndConfirmLabel()
    {
        var window = new ConfirmWindow(
            "删除 3 张图片？", "文件将移到回收站。", "a.jpg", "移到回收站");

        Assert.Equal("删除 3 张图片？", window.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal("移到回收站", window.FindControl<Button>("ConfirmButton")!.Content);
        Assert.Equal("a.jpg", window.FindControl<TextBlock>("DetailText")!.Text);

        window.Close();
    }
}
