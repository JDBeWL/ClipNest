using Avalonia;
using Avalonia.Media;
using ClipNest.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipNest.App.Theme;

/// <summary>设置页字体下拉框里的单个候选项。</summary>
public sealed partial class FontChoice : ObservableObject
{
    public FontChoice(
        string id,
        string displayName,
        FontFamily previewFontFamily,
        bool isRecommended = false,
        bool isMissing = false)
    {
        Id = id;
        _displayName = displayName;
        PreviewFontFamily = previewFontFamily;
        IsRecommended = isRecommended;
        IsMissing = isMissing;
    }

    /// <summary>写入设置的稳定标识；取预设名或系统字体族名。</summary>
    public string Id { get; }

    /// <summary>展示给用户的字体名称。切换语言时就地更新，避免重建集合导致下拉框失去选中项。</summary>
    [ObservableProperty] private string _displayName;

    /// <summary>下拉项预览使用的字体，缺字由全局字体回退链兜底。</summary>
    public FontFamily PreviewFontFamily { get; }

    public bool IsRecommended { get; }

    /// <summary>配置里的字体当前未安装在本机。</summary>
    public bool IsMissing { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 应用的字体与字号单一事实源。XAML 通过 DynamicResource 读取资源字典里的键值，
/// 自绘控件读取 <see cref="Ui"/> 与 <see cref="Version"/>，切换字体后整窗同步刷新。
/// </summary>
public static class AppFonts
{
    /// <summary>随程序分发的静态思源黑体 URI，避免命中系统里只暴露 Thin 字重的可变字体。</summary>
    public const string BundledNotoUri = "avares://ClipNest.App/Assets/Fonts#Noto Sans SC";

    private const string DefaultZhUiFont = "Microsoft YaHei UI";
    private const string DefaultJaUiFont = "Yu Gothic UI";
    private const string DefaultEnUiFont = "Segoe UI";

    private static readonly string? DetectedOsUiFont = DetectSystemUiFontName();

    private static string ResolveSystemFontName()
    {
        var culture = AppLocalization.CurrentCultureCode;
        if (string.Equals(culture, LanguageModes.JaJp, StringComparison.OrdinalIgnoreCase))
            return DefaultJaUiFont;
        if (string.Equals(culture, LanguageModes.EnUs, StringComparison.OrdinalIgnoreCase))
            return DefaultEnUiFont;

        if (!string.IsNullOrEmpty(DetectedOsUiFont) &&
            !string.Equals(DetectedOsUiFont, "Segoe UI", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DetectedOsUiFont, "Segoe UI Variable", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DetectedOsUiFont, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DetectedOsUiFont, "SimSun", StringComparison.OrdinalIgnoreCase))
        {
            return DetectedOsUiFont;
        }

        return DefaultZhUiFont;
    }

    // 中文 Windows 下 DirectWrite 会返回本地化族名，需要同时兼容英文与中文别名。
    private static readonly (string[] Aliases, string DisplayName)[] CuratedSystemFonts =
    {
        (["Microsoft YaHei UI"], "微软雅黑 UI"),
        (["Microsoft YaHei", "微软雅黑"], "微软雅黑"),
        (["DengXian", "等线"], "等线"),
        (["Yu Gothic UI"], "Yu Gothic UI"),
        (["Meiryo"], "Meiryo"),
        (["HarmonyOS Sans SC"], "鸿蒙黑体 · HarmonyOS Sans SC"),
        (["MiSans"], "小米兰亭 · MiSans"),
        (["OPPO Sans"], "OPPO Sans"),
        (["PingFang SC"], "苹方 · PingFang SC"),
        (["Sarasa UI SC"], "更纱黑体 UI · Sarasa UI SC"),
        (["Sarasa Gothic SC"], "更纱黑体 · Sarasa Gothic SC"),
        (["LXGW WenKai Screen"], "霞鹜文楷屏幕版 · LXGW WenKai Screen"),
        (["LXGW WenKai"], "霞鹜文楷 · LXGW WenKai"),
        (["Source Han Sans SC", "思源黑体 CN"], "思源黑体 · Source Han Sans SC"),
        (["Inter"], "Inter"),
        (["Segoe UI Variable Text", "Segoe UI Variable"], "Segoe UI Variable"),
        (["Segoe UI"], "Segoe UI"),
        (["SimHei", "黑体"], "黑体 · SimHei"),
        (["KaiTi", "楷体"], "楷体 · KaiTi"),
        (["FangSong", "仿宋"], "仿宋 · FangSong"),
        (["SimSun", "宋体"], "宋体 · SimSun"),
    };

    private static readonly HashSet<string> ExcludedSystemFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Noto Sans SC",
        "HoloLens MDL2 Assets",
        "Marlett",
        "MT Extra",
        "Segoe Fluent Icons",
        "Segoe MDL2 Assets",
        "Segoe UI Emoji",
        "Segoe UI Historic",
        "Segoe UI Symbol",
        "Symbol",
        "Webdings",
        "Wingdings",
        "Wingdings 2",
        "Wingdings 3",
    };

    private static FontFamily _ui = BuildUiFamily(FontPresets.System);
    private static FontFamily _display = BuildDisplayFamily(FontPresets.System);

    public static FontFamily Ui => _ui;
    public static FontFamily Display => _display;
    public static string CurrentSelection { get; private set; } = FontPresets.System;
    public static string CurrentScale { get; private set; } = FontScales.Standard;
    public static double CanvasFontSize { get; private set; } = 12;
    public static double GalaxyLabelSize { get; private set; } = 14;

    /// <summary>字体或字号变化时递增，自绘控件借此判断是否需要重建 Typeface 与文本缓存。</summary>
    public static int Version { get; private set; }

    /// <summary>按用户选择构造正文与控件字体。单一字体名由 Skia 直接匹配，避免复合字体链跳过本地化中文族名。</summary>
    public static FontFamily BuildUiFamily(string? selection)
    {
        var normalized = FontPresets.Normalize(selection);
        return normalized switch
        {
            FontPresets.System => new FontFamily(ResolveSystemFontName()),
            FontPresets.Bundled => new FontFamily(BundledNotoUri),
            _ => new FontFamily(SanitizeFamilyName(normalized))
        };
    }

    /// <summary>按用户选择构造标题字体，与正文共用单一字体解析路径，缺字交由全局回退链处理。</summary>
    public static FontFamily BuildDisplayFamily(string? selection) => BuildUiFamily(selection);

    /// <summary>应用字体与字号并同步写入应用资源字典，供全部窗口通过 DynamicResource 实时生效。</summary>
    public static void Apply(string? selection, string? scale)
    {
        var normalizedFont = FontPresets.Normalize(selection);
        var normalizedScale = FontScales.Normalize(scale);

        _ui = BuildUiFamily(normalizedFont);
        _display = BuildDisplayFamily(normalizedFont);
        CurrentSelection = normalizedFont;
        CurrentScale = normalizedScale;

        var metrics = GetScaleMetrics(normalizedScale);
        CanvasFontSize = metrics.Caption;
        GalaxyLabelSize = metrics.Title;

        if (Application.Current is { } app)
        {
            var res = app.Resources;
            res["Font.UI"] = _ui;
            res["Font.Display"] = _display;
            res["FontSize.PageTitle"] = metrics.PageTitle;
            res["FontSize.Display"] = metrics.Display;
            res["FontSize.InspectorTitle"] = metrics.InspectorTitle;
            res["FontSize.Heading"] = metrics.Heading;
            res["FontSize.Title"] = metrics.Title;
            res["FontSize.Body"] = metrics.Body;
            res["FontSize.Nav"] = metrics.Nav;
            res["FontSize.Tag"] = metrics.Tag;
            res["FontSize.Control"] = metrics.Control;
            res["FontSize.Caption"] = metrics.Caption;
            res["FontSize.Micro"] = metrics.Micro;
            res["LineHeight.Body"] = metrics.BodyLineHeight;
            res["LineHeight.Caption"] = metrics.CaptionLineHeight;
        }

        Version++;
    }

    /// <summary>发现可供选择的字体列表。内置预设置顶，其后列出本机常用中英文字体与其余系统字体。</summary>
    public static IReadOnlyList<FontChoice> DiscoverChoices(string? currentSelection)
    {
        IEnumerable<string> systemNames;
        try
        {
            systemNames = FontManager.Current.SystemFonts.Select(family => family.Name);
        }
        catch
        {
            systemNames = Array.Empty<string>();
        }

        return BuildChoices(systemNames, currentSelection);
    }

    /// <summary>根据给定的字体名集合筛选并排序，组装设置页下拉框候选项。</summary>
    public static IReadOnlyList<FontChoice> BuildChoices(
        IEnumerable<string> installedFontNames, string? currentSelection)
    {
        var choices = new List<FontChoice>
        {
            new(FontPresets.System, AppLocalization.Get("Settings.Font.PresetSystem"),
                BuildUiFamily(FontPresets.System), isRecommended: true),
            new(FontPresets.Bundled, AppLocalization.Get("Settings.Font.PresetBundled"),
                BuildUiFamily(FontPresets.Bundled), isRecommended: true),
        };

        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in installedFontNames)
        {
            var name = SanitizeFamilyName(raw);
            if (name.Length == 0 || name.StartsWith('@') || ExcludedSystemFonts.Contains(name))
                continue;
            installed.TryAdd(name, name);
        }

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (aliases, displayName) in CuratedSystemFonts)
        {
            string? exact = null;
            foreach (var alias in aliases)
            {
                if (installed.TryGetValue(alias, out exact)) break;
            }

            if (exact is null || !addedDisplayNames.Add(displayName)) continue;

            // 同一字体的其余别名也记入已添加集合，避免系统同时上报中英文名时出现重复项。
            foreach (var alias in aliases)
            {
                if (installed.TryGetValue(alias, out var installedAlias))
                    added.Add(installedAlias);
            }

            choices.Add(new FontChoice(exact, displayName, BuildUiFamily(exact), isRecommended: true));
        }

        var remaining = installed.Values
            .Where(name => !added.Contains(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var name in remaining)
        {
            choices.Add(new FontChoice(name, name, BuildUiFamily(name)));
        }

        var normalizedCurrent = FontPresets.Normalize(currentSelection);
        if (normalizedCurrent != FontPresets.System &&
            normalizedCurrent != FontPresets.Bundled &&
            !choices.Any(c => string.Equals(c.Id, normalizedCurrent, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new FontChoice(
                normalizedCurrent,
                AppLocalization.Format("Settings.Font.NotInstalled", normalizedCurrent),
                BuildUiFamily(normalizedCurrent),
                isMissing: true));
        }

        return choices;
    }

    /// <summary>按当前语言就地刷新已有字体候选项的展示名，不重建集合。</summary>
    public static void RefreshChoiceLabels(IEnumerable<FontChoice> choices)
    {
        foreach (var choice in choices)
        {
            if (string.Equals(choice.Id, FontPresets.System, StringComparison.OrdinalIgnoreCase))
            {
                choice.DisplayName = AppLocalization.Get("Settings.Font.PresetSystem");
            }
            else if (string.Equals(choice.Id, FontPresets.Bundled, StringComparison.OrdinalIgnoreCase))
            {
                choice.DisplayName = AppLocalization.Get("Settings.Font.PresetBundled");
            }
            else if (choice.IsMissing)
            {
                choice.DisplayName = AppLocalization.Format("Settings.Font.NotInstalled", choice.Id);
            }
        }
    }

    public static TypographyScaleMetrics GetScaleMetrics(string? scale) => FontScales.Normalize(scale) switch
    {
        FontScales.Compact => new TypographyScaleMetrics(
            PageTitle: 20,
            Display: 18,
            InspectorTitle: 15,
            Heading: 14,
            Title: 13,
            Body: 12,
            Nav: 12,
            Tag: 11.5,
            Control: 11.5,
            Caption: 11.5,
            Micro: 10.5,
            BodyLineHeight: 19,
            CaptionLineHeight: 17),
        FontScales.Comfortable => new TypographyScaleMetrics(
            PageTitle: 24,
            Display: 21,
            InspectorTitle: 17.5,
            Heading: 16.5,
            Title: 15.5,
            Body: 14.5,
            Nav: 14.5,
            Tag: 13.5,
            Control: 13.5,
            Caption: 13,
            Micro: 12,
            BodyLineHeight: 22,
            CaptionLineHeight: 20),
        _ => new TypographyScaleMetrics(
            PageTitle: 22,
            Display: 19,
            InspectorTitle: 16,
            Heading: 15,
            Title: 14,
            Body: 13,
            Nav: 13,
            Tag: 12.5,
            Control: 12.5,
            Caption: 12,
            Micro: 11,
            BodyLineHeight: 20,
            CaptionLineHeight: 18),
    };

    /// <summary>读取 Windows 系统消息框字体名，用户自定义系统界面字体时优先沿用。</summary>
    private static string? DetectSystemUiFontName()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var font = System.Drawing.SystemFonts.MessageBoxFont;
            var name = SanitizeFamilyName(font?.Name);
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>过滤逗号与井号，防止非法字体名破坏 Avalonia 复合字体语法。</summary>
    private static string SanitizeFamilyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        return name.Replace(",", " ").Replace("#", " ").Trim();
    }
}

/// <summary>单个字号档位对应的完整字号与行高表。</summary>
public readonly record struct TypographyScaleMetrics(
    double PageTitle,
    double Display,
    double InspectorTitle,
    double Heading,
    double Title,
    double Body,
    double Nav,
    double Tag,
    double Control,
    double Caption,
    double Micro,
    double BodyLineHeight,
    double CaptionLineHeight);
