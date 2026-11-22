using Avalonia.Media;

namespace ClipNest.App.Theme;

/// <summary>
/// ClipNest 色板，macOS 深色取向的中性石墨与系统蓝强调色。导航层使用半透明玻璃，叠在模糊环境图上；
/// 内容层为不透明实色，保证图片对比稳定。分隔使用 8% 白细线而非带边框的卡片，
/// 强调色只用于选中项与主操作，语义色沿用 Apple 系统色。
/// </summary>
public static class AppPalette
{
    // 表面色，整窗共享一层半透明玻璃底，细节靠悬浮面板拉开层次。
    public static readonly Color SurfaceWindow = Color.Parse("#101114");
    public static readonly Color SurfaceContent = Color.Parse("#101115");
    public static readonly Color SurfaceShell = Color.Parse("#B8141418");
    public static readonly Color SurfaceGlass = Color.Parse("#A61C1C22");
    public static readonly Color SurfaceNavigation = Color.Parse("#BF1B1C21");
    public static readonly Color SurfaceStatusBar = Color.Parse("#BF1B1C21");
    public static readonly Color SurfaceElevated = Color.Parse("#17FFFFFF");
    public static readonly Color SurfaceCard = Color.Parse("#0FFFFFFF");
    public static readonly Color SurfaceHover = Color.Parse("#14FFFFFF");
    public static readonly Color SurfaceActive = Color.Parse("#24FFFFFF");
    public static readonly Color SurfaceOrnament = Color.Parse("#F21A1B20");
    public static readonly Color SurfacePopup = Color.Parse("#F2212227");
    public static readonly Color SurfaceCanvas = Color.Parse("#00000000");

    // 细分隔线。
    public static readonly Color BorderSubtle = Color.Parse("#14FFFFFF");
    public static readonly Color BorderStrong = Color.Parse("#2BFFFFFF");

    // 控件槽位，用于分段控件与搜索框。
    public static readonly Color ControlWell = Color.Parse("#12FFFFFF");
    public static readonly Color ControlThumb = Color.Parse("#2BFFFFFF");

    // 文本色，四级，纯中性。
    public static readonly Color TextPrimary = Color.Parse("#FFFFFF");
    public static readonly Color TextSecondary = Color.Parse("#C7CAD1");
    public static readonly Color TextTertiary = Color.Parse("#94989F");
    public static readonly Color TextDisabled = Color.Parse("#5A5E67");
    public static readonly Color TextInverse = Color.Parse("#0A0B0E");

    // 强调色，取 macOS 系统蓝。
    public static readonly Color Accent = Color.Parse("#0A84FF");
    public static readonly Color AccentHover = Color.Parse("#3D9BFF");
    public static readonly Color AccentPressed = Color.Parse("#0069D9");
    public static readonly Color AccentBright = Color.Parse("#7CB8FF");
    public static readonly Color AccentOn = Color.Parse("#FFFFFF");
    public static readonly Color AccentSubtle = Color.Parse("#330A84FF");
    public static readonly Color AccentSubtleHover = Color.Parse("#4D0A84FF");
    public static readonly Color AccentFaint = Color.Parse("#1A0A84FF");
    public static readonly Color AccentGlow = Color.FromArgb(0x59, 0x0A, 0x84, 0xFF);

    // 选中态与覆盖层。
    public static readonly Color SelectionFill = Color.FromArgb(0x3D, 0x0A, 0x84, 0xFF);
    public static readonly Color SelectionOuter = Color.FromArgb(0xCC, 0x05, 0x07, 0x0A);
    public static readonly Color HoverOutline = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
    public static readonly Color Scrim = Color.FromArgb(0x59, 0x08, 0x09, 0x0C);
    // 图片上的文件名遮罩，与环境层的 Scrim 分开，保证亮图上也读得清。
    public static readonly Color TileScrim = Color.FromArgb(0xB8, 0x06, 0x07, 0x0A);
    public static readonly Color FadeClear = Color.FromArgb(0x00, 0x14, 0x14, 0x18);
    public static readonly Color FadeOpaque = Color.FromArgb(0xA6, 0x14, 0x14, 0x18);
    public static readonly Color TitleBarScrim = Color.FromArgb(0xC4, 0x0D, 0x0E, 0x11);
    public static readonly Color OverlayButton = Color.FromArgb(0xD9, 0x14, 0x15, 0x19);
    // 压在深色遮罩上的文字恒定用浅色；遮罩不随主题变，文字也不能变。
    public static readonly Color OverlayText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    public static readonly Color OverlayTextMuted = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
    public static readonly Color ScrollThumb = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
    public static readonly Color ScrollThumbHover = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
    public static readonly Color Star = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF);
    public static readonly Color Shadow = Color.FromArgb(0x73, 0x00, 0x00, 0x00);
    public static readonly Color AmbientTint = Color.FromArgb(0x14, 0x3A, 0x6E, 0xA5);

    // 语义状态色，沿用 Apple 系统色。
    public static readonly Color Success = Color.Parse("#30D158");
    public static readonly Color SuccessSubtle = Color.Parse("#2630D158");
    public static readonly Color Warning = Color.Parse("#FFD60A");
    public static readonly Color WarningSubtle = Color.Parse("#26FFD60A");
    public static readonly Color Danger = Color.Parse("#FF453A");
    public static readonly Color DangerSubtle = Color.Parse("#26FF453A");

    // 品牌色，只用于应用标记。
    public static readonly Color BrandStart = Color.Parse("#0A84FF");
    public static readonly Color BrandEnd = Color.Parse("#5E5CE6");
}
