using Avalonia;
using Avalonia.Media;

namespace ClipNest.App.Theme;

/// <summary>
/// 自绘控件使用的字体。App.axaml 的 Font.UI 与 Font.Display 是唯一来源，
/// 代码里另写一份回退链会与 XAML 漂移。
/// </summary>
public static class AppFonts
{
    private static FontFamily? _ui;

    public static FontFamily Ui => _ui ??= Resolve("Font.UI");

    private static FontFamily Resolve(string key)
        => Application.Current is { } app &&
           app.Resources.TryGetResource(key, null, out var value) &&
           value is FontFamily family
            ? family
            : FontFamily.Default;
}
