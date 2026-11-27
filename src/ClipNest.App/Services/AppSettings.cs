using System.Globalization;

namespace ClipNest.App.Services;

public sealed class AppSettings
{
    public int UiVersion { get; set; }
    public string LastView { get; set; } = "Grid";
    public double WallMotion { get; set; } = 1.0;
    public bool SemanticEnabled { get; set; }
    public bool InspectorOpen { get; set; } = true;
    public double ThumbnailSize { get; set; } = 200;
    public string SortMode { get; set; } = "default";

    /// <summary>外观模式，取 Dark、Light 或 System。默认跟随系统深浅色。</summary>
    public string Appearance { get; set; } = AppearanceModes.System;

    /// <summary>界面语言标识，取 System 或语言包代码（如 zh-CN、en-US、ja-JP）。默认跟随系统。</summary>
    public string Language { get; set; } = LanguageModes.System;

    /// <summary>界面字体标识；取 System、Bundled 或系统已安装的字体族名。</summary>
    public string FontFamily { get; set; } = FontPresets.System;

    /// <summary>全局字号档位，取 Compact、Standard 或 Comfortable。</summary>
    public string FontScale { get; set; } = FontScales.Standard;

    /// <summary>是否自动识别新图片并写入 AI 标签；关闭后队列暂停领取新任务。</summary>
    public bool AutoTaggingEnabled { get; set; } = true;

    /// <summary>默认识别模型 ID；为空时使用最新的 CAMIE 模型。</summary>
    public string TagModelId { get; set; } = "";

    /// <summary>是否自动为新图片生成语义向量；关闭后索引队列暂停领取新任务。</summary>
    public bool AutoIndexingEnabled { get; set; } = true;

    /// <summary>ONNX 推理线程数；0 表示按逻辑核心数取一半。</summary>
    public int InferenceThreads { get; set; }

    /// <summary>
    /// 普通状态下的窗口位置与尺寸。位置是物理像素，尺寸是 DIP；
    /// 为空表示第一次启动，交给系统按工作区居中。
    /// </summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}

/// <summary>外观模式的取值与规范化，避免字符串散落在各处。</summary>
public static class AppearanceModes
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string System = "System";

    public static string Normalize(string? value)
        => value is Light or Dark ? value : System;
}

/// <summary>界面字体预设标识与规范化。系统里的 Noto Sans SC 可变字体只暴露 Thin，统一映射到内置静态字体。</summary>
public static class FontPresets
{
    public const string System = "System";
    public const string Bundled = "Bundled";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return System;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, System, StringComparison.OrdinalIgnoreCase)) return System;
        if (string.Equals(trimmed, Bundled, StringComparison.OrdinalIgnoreCase)) return Bundled;
        if (string.Equals(trimmed, "Noto Sans SC", StringComparison.OrdinalIgnoreCase)) return Bundled;
        return trimmed;
    }
}

/// <summary>全局字号档位的取值与规范化。</summary>
public static class FontScales
{
    public const string Compact = "Compact";
    public const string Standard = "Standard";
    public const string Comfortable = "Comfortable";

    public static string Normalize(string? value)
        => value is Compact or Comfortable ? value : Standard;
}

/// <summary>界面语言标识与系统文化解析。</summary>
public static class LanguageModes
{
    public const string System = "System";
    public const string ZhCn = "zh-CN";
    public const string EnUs = "en-US";
    public const string JaJp = "ja-JP";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return System;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, System, StringComparison.OrdinalIgnoreCase)) return System;
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return ZhCn;
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return EnUs;
        if (trimmed.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return JaJp;
        return AppLocalization.HasLocale(trimmed) ? AppLocalization.CanonicalLocaleCode(trimmed) : System;
    }

    /// <summary>将配置值落到具体语言包代码；跟随系统时中文与日文分别匹配，其余回落英文。</summary>
    public static string Resolve(string? value, CultureInfo? systemCulture = null)
    {
        var normalized = Normalize(value);
        if (normalized != System) return normalized;

        var culture = systemCulture ?? CultureInfo.CurrentUICulture;
        var name = culture.Name;
        if (AppLocalization.HasLocale(name)) return AppLocalization.CanonicalLocaleCode(name);
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return ZhCn;
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return JaJp;
        return EnUs;
    }
}
