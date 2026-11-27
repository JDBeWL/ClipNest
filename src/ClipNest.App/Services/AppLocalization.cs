using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipNest.App.Services;

/// <summary>设置页语言下拉框里的单个候选项。</summary>
public sealed partial class LanguageChoice : ObservableObject
{
    public LanguageChoice(string id, string displayName)
    {
        Id = id;
        _displayName = displayName;
    }

    /// <summary>写入设置的语言标识；取 System 或语言包代码。</summary>
    public string Id { get; }

    /// <summary>展示给用户的语言名称。切换语言时就地更新，避免重建集合导致下拉框失去选中项。</summary>
    [ObservableProperty] private string _displayName;

    public override string ToString() => DisplayName;
}

/// <summary>
/// 应用的多语言加载与热切换服务。
/// 文案从 <c>Assets/Locales/*.json</c> 载入，优先读取磁盘目录以便外部增改，缺失时回落程序集内嵌语言包。
/// 切换语言时同步替换应用资源字典里的 <c>Str.*</c> 键，使全部已打开窗口立即更新。
/// </summary>
public static class AppLocalization
{
    /// <summary>XAML 动态资源的统一前缀，例如 <c>Str.Nav.AllImages</c>。</summary>
    public const string ResourcePrefix = "Str.";

    /// <summary>语言包内的自述名称键，供自定义语言包声明展示名。</summary>
    public const string MetaNameKey = "_meta.name";

    private const string EmbeddedPrefix = "ClipNest.App.Assets.Locales.";
    private const string FallbackLocale = LanguageModes.ZhCn;

    private static readonly string[] BuiltInOrder =
    {
        LanguageModes.ZhCn,
        LanguageModes.EnUs,
        LanguageModes.JaJp
    };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Locales =
        LoadAllLocales();

    private static IReadOnlyDictionary<string, string> _fallback =
        Locales.TryGetValue(FallbackLocale, out var zh)
            ? zh
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> _active = _fallback;
    private static ResourceDictionary? _activeResourceDictionary;

    static AppLocalization()
    {
        var resolved = LanguageModes.Resolve(LanguageModes.System);
        CurrentSelection = LanguageModes.System;
        CurrentCultureCode = resolved;
        CurrentCulture = CreateCulture(resolved);
        _active = Locales.TryGetValue(resolved, out var table) ? table : _fallback;
    }

    public static string CurrentSelection { get; private set; }

    public static string CurrentCultureCode { get; private set; }

    public static CultureInfo CurrentCulture { get; private set; }

    /// <summary>语言切换时递增，自绘控件借此清理文本布局缓存。</summary>
    public static int Version { get; private set; }

    public static event Action? LanguageChanged;

    public static IReadOnlyList<string> AvailableLocaleCodes =>
        BuiltInOrder
            .Where(Locales.ContainsKey)
            .Concat(Locales.Keys
                .Where(code => !BuiltInOrder.Contains(code, StringComparer.OrdinalIgnoreCase))
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    public static bool HasLocale(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Locales.ContainsKey(code.Trim());

    public static string CanonicalLocaleCode(string code)
    {
        var trimmed = code.Trim();
        foreach (var existing in Locales.Keys)
        {
            if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                return existing;
        }

        return trimmed;
    }

    public static IReadOnlyDictionary<string, string> GetTable(string localeCode)
    {
        if (Locales.TryGetValue(localeCode, out var table)) return table;
        return _fallback;
    }

    /// <summary>按当前语言读取文案；缺失键回落简体中文，仍不存在时返回键名本身。</summary>
    public static string Get(string key)
    {
        if (_active.TryGetValue(key, out var value)) return value;
        if (_fallback.TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    /// <summary>按指定语言代码读取文案，不改变全局当前语言。</summary>
    public static string Get(string key, string localeCode)
    {
        if (Locales.TryGetValue(localeCode, out var table) && table.TryGetValue(key, out var value))
            return value;
        if (_fallback.TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    /// <summary>按当前文化格式化带占位符的文案。</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CurrentCulture, Get(key), args);

    /// <summary>按数量选择单复数键并格式化。</summary>
    public static string FormatCount(string singularKey, string pluralKey, int count) =>
        count == 1 ? Get(singularKey) : Format(pluralKey, count);

    /// <summary>构造设置页语言下拉项。首项跟随系统，其余项保留各语言自称以便误切后识别。</summary>
    public static IReadOnlyList<LanguageChoice> BuildChoices()
    {
        var choices = new List<LanguageChoice>
        {
            new(LanguageModes.System, Get("Settings.Language.System"))
        };

        foreach (var code in AvailableLocaleCodes)
        {
            var table = Locales[code];
            var displayName = table.TryGetValue(MetaNameKey, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : code;
            choices.Add(new LanguageChoice(code, displayName));
        }

        return choices;
    }

    /// <summary>按当前语言就地刷新已有语言候选项的展示名，不重建集合。</summary>
    public static void RefreshChoiceLabels(IEnumerable<LanguageChoice> choices)
    {
        foreach (var choice in choices)
        {
            if (string.Equals(choice.Id, LanguageModes.System, StringComparison.OrdinalIgnoreCase))
            {
                choice.DisplayName = Get("Settings.Language.System");
            }
        }
    }

    /// <summary>切换当前语言并同步写入应用动态资源，使已打开的窗口与菜单立即刷新。</summary>
    public static void Apply(string? selection, CultureInfo? systemCulture = null)
    {
        var normalized = LanguageModes.Normalize(selection);
        var resolved = LanguageModes.Resolve(normalized, systemCulture);

        lock (Sync)
        {
            CurrentSelection = normalized;
            CurrentCultureCode = resolved;
            CurrentCulture = CreateCulture(resolved);
            _active = Locales.TryGetValue(resolved, out var table) ? table : _fallback;
            Version++;

            if (Application.Current is { } app)
            {
                ApplyToResourcesCore(app.Resources);
            }
        }

        LanguageChanged?.Invoke();
    }

    /// <summary>将当前语言词典挂入应用资源字典，供 <see cref="App.Initialize"/> 首次加载使用。</summary>
    public static void AttachToApplication(Application app)
    {
        lock (Sync)
        {
            ApplyToResourcesCore(app.Resources);
        }
    }

    private static void ApplyToResourcesCore(IResourceDictionary resources)
    {
        var next = new ResourceDictionary();
        foreach (var (key, value) in _fallback)
        {
            if (key.StartsWith("_meta.", StringComparison.Ordinal)) continue;
            next[ResourcePrefix + key] = value;
        }

        foreach (var (key, value) in _active)
        {
            if (key.StartsWith("_meta.", StringComparison.Ordinal)) continue;
            next[ResourcePrefix + key] = value;
        }

        if (_activeResourceDictionary is not null)
        {
            resources.MergedDictionaries.Remove(_activeResourceDictionary);
        }

        resources.MergedDictionaries.Add(next);
        _activeResourceDictionary = next;
    }

    private static CultureInfo CreateCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadAllLocales()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // 先加载内嵌语言包，保证无外部语言包文件与单文件发布环境也可正常使用。
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = resourceName[EmbeddedPrefix.Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            var parsed = ParseJson(stream);
            if (parsed is not null) result[code] = parsed;
        }

        // 再读取输出目录的磁盘语言包，允许用户原地微调或增补新语言文件。
        var diskDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Locales");
        if (Directory.Exists(diskDir))
        {
            foreach (var file in Directory.EnumerateFiles(diskDir, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(code)) continue;

                try
                {
                    using var stream = File.OpenRead(file);
                    var parsed = ParseJson(stream);
                    if (parsed is not null) result[code] = parsed;
                }
                catch
                {
                    // 外部语言包损坏时跳过，继续保留内嵌回退。
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string>? ParseJson(Stream stream)
    {
        var raw = JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.DictionaryStringString);
        if (raw is null) return null;

        var copy = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                copy[key] = value;
            }
        }

        return copy;
    }
}
