using Avalonia.Media;
using Avalonia.Styling;

namespace ClipNest.App.Theme;

/// <summary>
/// 当前生效的色板，供自绘控件在绘制时读取颜色。
/// </summary>
/// <remarks>
/// XAML 侧由 App.axaml 的 ThemeDictionaries 提供同一套值，切换由 Avalonia 处理；自绘控件拿不到
/// 资源字典里的画刷，只能读这里的颜色。两边的值必须一致，App.axaml 的 Dark 与 Light 字典引用的
/// 正是 <see cref="AppPalette"/> 与 <see cref="AppPaletteLight"/> 的字段，不存在第二份硬编码。
/// </remarks>
public static class ThemeColors
{
    private static bool _isLight;
    private static bool _applied;

    public static bool IsLight => _isLight;

    /// <summary>
    /// 每次变体生效时递增。自绘控件缓存了画刷与画笔，靠它判断要不要重建；
    /// 用版本号而不是事件，控件在构造、切树、重建时都不会漏掉或重复订阅。
    /// </summary>
    public static int Version { get; private set; }

    /// <summary>主题真正发生变化时触发，首次应用不触发。</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 由 App 在启动时与变体切换时调用。重复应用同一个变体不会重复广播，
    /// 避免每次窗口激活都触发一轮全量重绘。
    /// </summary>
    public static void Apply(ThemeVariant variant)
    {
        var light = variant == ThemeVariant.Light;
        if (_applied && light == _isLight) return;

        var first = !_applied;
        _isLight = light;
        _applied = true;
        Version++;

        if (!first) Changed?.Invoke(null, EventArgs.Empty);
    }

    // 表面色。
    public static Color SurfaceWindow => Pick(AppPalette.SurfaceWindow, AppPaletteLight.SurfaceWindow);
    public static Color SurfaceContent => Pick(AppPalette.SurfaceContent, AppPaletteLight.SurfaceContent);
    public static Color SurfaceShell => Pick(AppPalette.SurfaceShell, AppPaletteLight.SurfaceShell);
    public static Color SurfaceGlass => Pick(AppPalette.SurfaceGlass, AppPaletteLight.SurfaceGlass);
    public static Color SurfaceNavigation => Pick(AppPalette.SurfaceNavigation, AppPaletteLight.SurfaceNavigation);
    public static Color SurfaceStatusBar => Pick(AppPalette.SurfaceStatusBar, AppPaletteLight.SurfaceStatusBar);
    public static Color SurfaceElevated => Pick(AppPalette.SurfaceElevated, AppPaletteLight.SurfaceElevated);
    public static Color SurfaceCard => Pick(AppPalette.SurfaceCard, AppPaletteLight.SurfaceCard);
    public static Color SurfaceHover => Pick(AppPalette.SurfaceHover, AppPaletteLight.SurfaceHover);
    public static Color SurfaceActive => Pick(AppPalette.SurfaceActive, AppPaletteLight.SurfaceActive);
    public static Color SurfaceOrnament => Pick(AppPalette.SurfaceOrnament, AppPaletteLight.SurfaceOrnament);
    public static Color SurfacePopup => Pick(AppPalette.SurfacePopup, AppPaletteLight.SurfacePopup);
    public static Color SurfaceCanvas => Pick(AppPalette.SurfaceCanvas, AppPaletteLight.SurfaceCanvas);

    // 分隔与槽位。
    public static Color BorderSubtle => Pick(AppPalette.BorderSubtle, AppPaletteLight.BorderSubtle);
    public static Color BorderStrong => Pick(AppPalette.BorderStrong, AppPaletteLight.BorderStrong);
    public static Color ControlWell => Pick(AppPalette.ControlWell, AppPaletteLight.ControlWell);
    public static Color ControlThumb => Pick(AppPalette.ControlThumb, AppPaletteLight.ControlThumb);

    // 文本。
    public static Color TextPrimary => Pick(AppPalette.TextPrimary, AppPaletteLight.TextPrimary);
    public static Color TextSecondary => Pick(AppPalette.TextSecondary, AppPaletteLight.TextSecondary);
    public static Color TextTertiary => Pick(AppPalette.TextTertiary, AppPaletteLight.TextTertiary);
    public static Color TextDisabled => Pick(AppPalette.TextDisabled, AppPaletteLight.TextDisabled);
    public static Color TextInverse => Pick(AppPalette.TextInverse, AppPaletteLight.TextInverse);

    // 强调。
    public static Color Accent => Pick(AppPalette.Accent, AppPaletteLight.Accent);
    public static Color AccentHover => Pick(AppPalette.AccentHover, AppPaletteLight.AccentHover);
    public static Color AccentPressed => Pick(AppPalette.AccentPressed, AppPaletteLight.AccentPressed);
    public static Color AccentBright => Pick(AppPalette.AccentBright, AppPaletteLight.AccentBright);
    public static Color AccentOn => Pick(AppPalette.AccentOn, AppPaletteLight.AccentOn);
    public static Color AccentSubtle => Pick(AppPalette.AccentSubtle, AppPaletteLight.AccentSubtle);
    public static Color AccentSubtleHover => Pick(AppPalette.AccentSubtleHover, AppPaletteLight.AccentSubtleHover);
    public static Color AccentFaint => Pick(AppPalette.AccentFaint, AppPaletteLight.AccentFaint);
    public static Color AccentGlow => Pick(AppPalette.AccentGlow, AppPaletteLight.AccentGlow);

    // 选中与覆盖。
    public static Color SelectionFill => Pick(AppPalette.SelectionFill, AppPaletteLight.SelectionFill);
    public static Color SelectionOuter => Pick(AppPalette.SelectionOuter, AppPaletteLight.SelectionOuter);
    public static Color HoverOutline => Pick(AppPalette.HoverOutline, AppPaletteLight.HoverOutline);
    public static Color Scrim => Pick(AppPalette.Scrim, AppPaletteLight.Scrim);
    public static Color TileScrim => Pick(AppPalette.TileScrim, AppPaletteLight.TileScrim);
    public static Color FadeClear => Pick(AppPalette.FadeClear, AppPaletteLight.FadeClear);
    public static Color FadeOpaque => Pick(AppPalette.FadeOpaque, AppPaletteLight.FadeOpaque);
    public static Color TitleBarScrim => Pick(AppPalette.TitleBarScrim, AppPaletteLight.TitleBarScrim);
    public static Color OverlayButton => Pick(AppPalette.OverlayButton, AppPaletteLight.OverlayButton);
    public static Color OverlayText => Pick(AppPalette.OverlayText, AppPaletteLight.OverlayText);
    public static Color OverlayTextMuted => Pick(AppPalette.OverlayTextMuted, AppPaletteLight.OverlayTextMuted);
    public static Color ScrollThumb => Pick(AppPalette.ScrollThumb, AppPaletteLight.ScrollThumb);
    public static Color ScrollThumbHover => Pick(AppPalette.ScrollThumbHover, AppPaletteLight.ScrollThumbHover);
    public static Color Star => Pick(AppPalette.Star, AppPaletteLight.Star);
    public static Color Shadow => Pick(AppPalette.Shadow, AppPaletteLight.Shadow);
    public static Color AmbientTint => Pick(AppPalette.AmbientTint, AppPaletteLight.AmbientTint);

    // 语义状态。
    public static Color Success => Pick(AppPalette.Success, AppPaletteLight.Success);
    public static Color SuccessSubtle => Pick(AppPalette.SuccessSubtle, AppPaletteLight.SuccessSubtle);
    public static Color Warning => Pick(AppPalette.Warning, AppPaletteLight.Warning);
    public static Color WarningSubtle => Pick(AppPalette.WarningSubtle, AppPaletteLight.WarningSubtle);
    public static Color Danger => Pick(AppPalette.Danger, AppPaletteLight.Danger);
    public static Color DangerSubtle => Pick(AppPalette.DangerSubtle, AppPaletteLight.DangerSubtle);

    // 品牌。
    public static Color BrandStart => Pick(AppPalette.BrandStart, AppPaletteLight.BrandStart);
    public static Color BrandEnd => Pick(AppPalette.BrandEnd, AppPaletteLight.BrandEnd);

    private static Color Pick(Color dark, Color light) => _isLight ? light : dark;
}
