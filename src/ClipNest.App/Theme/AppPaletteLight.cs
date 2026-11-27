using Avalonia.Media;

namespace ClipNest.App.Theme;

/// <summary>
/// 浅色色板，字段与 <see cref="AppPalette"/> 一一对应。
/// </summary>
/// <remarks>
/// 浅色不是深色的逐值取反。「浮起」在深色里由白色半透明表示，在浅色里改由黑色半透明表示；
/// 白底加白不可见，因此 <c>SurfaceElevated</c> 一类必须改用低透明度黑色。
/// 压在图片上的遮罩、角标与评分星保持深色，因为图片亮度不随界面主题变化。
/// </remarks>
public static class AppPaletteLight
{
    // 表面色。窗口底比纯白略灰，内容区才是纯白，界面与图片之间有层次。
    public static readonly Color SurfaceWindow = Color.Parse("#ECECEF");
    public static readonly Color SurfaceContent = Color.Parse("#FFFFFF");
    public static readonly Color SurfaceShell = Color.Parse("#F0F7F7FA");
    public static readonly Color SurfaceGlass = Color.Parse("#E6FFFFFF");
    public static readonly Color SurfaceNavigation = Color.Parse("#E6EFEFF2");
    public static readonly Color SurfaceInspector = Color.Parse("#80F5F6F9");
    public static readonly Color SurfaceStatusBar = Color.Parse("#E6EFEFF2");
    public static readonly Color SurfaceElevated = Color.Parse("#0F000000");
    public static readonly Color SurfaceCard = Color.Parse("#08000000");
    public static readonly Color SurfaceHover = Color.Parse("#0F000000");
    public static readonly Color SurfaceActive = Color.Parse("#1A000000");
    public static readonly Color SurfaceOrnament = Color.Parse("#F2F2F2F5");
    public static readonly Color SurfacePopup = Color.Parse("#FFFFFFFF");
    public static readonly Color SurfaceCanvas = AppPalette.SurfaceCanvas;

    // 细分隔线。深色下是 8% 白，浅色下用 8% 黑。
    public static readonly Color BorderSubtle = Color.Parse("#14000000");
    public static readonly Color BorderStrong = Color.Parse("#29000000");

    // 控件槽位。槽位比滑块深，白色滑块才有层次。
    public static readonly Color ControlWell = Color.Parse("#14000000");
    public static readonly Color ControlThumb = Color.Parse("#FFFFFF");

    // 文本色，四级。纯中性，不用纯黑，避免在白底上过于刺眼。
    public static readonly Color TextPrimary = Color.Parse("#1D1D1F");
    public static readonly Color TextSecondary = Color.Parse("#4C4C52");
    public static readonly Color TextTertiary = Color.Parse("#86868B");
    public static readonly Color TextDisabled = Color.Parse("#B4B4B9");
    public static readonly Color TextInverse = Color.Parse("#FFFFFF");

    // 强调色。浅色模式的系统蓝比深色模式略深，AccentBright 也随之变深。
    // 深色里的亮蓝在浅底上读不清；这里的「亮」指相对底色的对比度，不是绝对亮度。
    public static readonly Color Accent = Color.Parse("#007AFF");
    public static readonly Color AccentHover = Color.Parse("#2B8CFF");
    public static readonly Color AccentPressed = Color.Parse("#0062CC");
    public static readonly Color AccentBright = Color.Parse("#0060DF");
    public static readonly Color AccentOn = Color.Parse("#FFFFFF");
    public static readonly Color AccentSubtle = Color.Parse("#1A007AFF");
    public static readonly Color AccentSubtleHover = Color.Parse("#29007AFF");
    public static readonly Color AccentFaint = Color.Parse("#0F007AFF");
    public static readonly Color AccentGlow = Color.FromArgb(0x59, 0x00, 0x7A, 0xFF);

    // 选中态。选中环外侧的衬底用白色；浅色界面里深色衬底会变成一圈黑边。
    public static readonly Color SelectionFill = Color.FromArgb(0x2E, 0x00, 0x7A, 0xFF);
    public static readonly Color SelectionOuter = Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
    public static readonly Color HoverOutline = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
    public static readonly Color TileRim = Color.FromArgb(0x18, 0x00, 0x00, 0x00);
    public static readonly Color OverlayButtonRim = AppPalette.OverlayButtonRim;

    // 压在图片上的遮罩保持深色，图片亮度与界面主题无关。
    public static readonly Color Scrim = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
    public static readonly Color TileScrim = AppPalette.TileScrim;
    public static readonly Color OverlayButton = AppPalette.OverlayButton;
    public static readonly Color OverlayText = AppPalette.OverlayText;
    public static readonly Color OverlayTextMuted = AppPalette.OverlayTextMuted;
    // 星系视图的星点仅作装饰，浅底上必须极淡，否则整屏呈现为脏点。
    public static readonly Color Star = Color.FromArgb(0x12, 0x00, 0x00, 0x00);

    // 内容区的上下渐隐要渐隐到内容底色，浅色下就是白色。
    public static readonly Color FadeClear = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
    public static readonly Color FadeOpaque = Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF);
    public static readonly Color TitleBarScrim = Color.FromArgb(0xC4, 0xF2, 0xF2, 0xF5);

    // 滚动条与阴影。浅色界面的阴影要轻得多，否则形成一圈脏边。
    public static readonly Color ScrollThumb = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
    public static readonly Color ScrollThumbHover = Color.FromArgb(0x59, 0x00, 0x00, 0x00);
    public static readonly Color Shadow = Color.FromArgb(0x26, 0x00, 0x00, 0x00);
    public static readonly Color AmbientTint = Color.FromArgb(0x0F, 0x3A, 0x6E, 0xA5);

    // 语义状态色。浅底上要压深，否则黄色警告与白字一样无法辨认。
    public static readonly Color Success = Color.Parse("#248A3D");
    public static readonly Color SuccessSubtle = Color.Parse("#1F248A3D");
    public static readonly Color Warning = Color.Parse("#B25000");
    public static readonly Color WarningSubtle = Color.Parse("#1FB25000");
    public static readonly Color Danger = Color.Parse("#D70015");
    public static readonly Color DangerSubtle = Color.Parse("#1FD70015");

    // 品牌色。
    public static readonly Color BrandStart = Color.Parse("#007AFF");
    public static readonly Color BrandEnd = Color.Parse("#5E5CE6");
}
