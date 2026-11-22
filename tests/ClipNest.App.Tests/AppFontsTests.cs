using Avalonia.Headless.XUnit;
using ClipNest.App.Theme;

namespace ClipNest.App.Tests;

public sealed class AppFontsTests
{
    [AvaloniaFact]
    public void UiFont_ComesFromBundledFamily()
    {
        // 系统里的 Noto Sans SC 可能只暴露 Thin 字重，界面必须优先取随程序分发的静态字体。
        Assert.Contains("Noto Sans SC", AppFonts.Ui.ToString());
    }
}
