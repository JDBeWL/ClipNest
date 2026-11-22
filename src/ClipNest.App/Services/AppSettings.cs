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

    /// <summary>是否自动识别新图片并写入 AI 标签；关闭后队列暂停领取新任务。</summary>
    public bool AutoTaggingEnabled { get; set; } = true;

    /// <summary>默认识别模型 ID；为空时使用最新的 CAMIE 模型。</summary>
    public string TagModelId { get; set; } = "";

    /// <summary>是否自动为新图片生成语义向量；关闭后索引队列暂停领取新任务。</summary>
    public bool AutoIndexingEnabled { get; set; } = true;

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
