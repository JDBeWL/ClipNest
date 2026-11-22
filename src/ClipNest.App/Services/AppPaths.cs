namespace ClipNest.App.Services;

/// <summary>
/// 解析并验证应用的数据与日志目录，避免在受限目录上直接崩溃。
/// 默认数据目录为应用所在目录下的 data 文件夹，环境变量 CLIPNEST_HOME 可覆盖。
/// </summary>
public static class AppPaths
{
    public static string ResolveDataRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CLIPNEST_HOME");
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return overrideRoot;

        var appData = Path.Combine(AppContext.BaseDirectory, "data");
        if (TryPrepareWritable(appData)) return appData;

        // 应用目录不可写，例如安装到 Program Files 时，退回用户目录。
        return ResolveLegacyDataRoot();
    }

    public static string ResolveLegacyDataRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipNest");

    /// <summary>
    /// 首次使用新数据目录时，从旧目录迁移数据库、设置、缩略图与模型。
    /// 构建输出路径含 bin 时不迁移，避免每次 Debug 与 Release 构建都带出旧图库。
    /// </summary>
    public static void TryMigrateLegacyData(string targetRoot)
    {
        if (IsBuildOutput(AppContext.BaseDirectory)) return;
        TryMigrateData(targetRoot, ResolveLegacyDataRoot());
    }

    /// <summary>判断启动目录是否位于构建输出，即路径中存在 bin 段。</summary>
    public static bool IsBuildOutput(string directory)
    {
        var parts = directory.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase));
    }

    public static void TryMigrateData(string targetRoot, string legacyRoot)
    {
        if (PathsEqual(targetRoot, legacyRoot)) return;
        if (File.Exists(Path.Combine(targetRoot, "clipnest.db"))) return;
        if (!File.Exists(Path.Combine(legacyRoot, "clipnest.db"))) return;

        try
        {
            CopyFile(Path.Combine(legacyRoot, "clipnest.db"), Path.Combine(targetRoot, "clipnest.db"));
            CopyFile(Path.Combine(legacyRoot, "settings.json"), Path.Combine(targetRoot, "settings.json"));
            CopyDirectory(Path.Combine(legacyRoot, "cache"), Path.Combine(targetRoot, "cache"));
            CopyDirectory(Path.Combine(legacyRoot, "models"), Path.Combine(targetRoot, "models"));
        }
        catch
        {
            // 迁移尽力而为；失败时使用新库启动，旧数据仍保留在原目录。
        }
    }

    /// <summary>优先使用数据目录下的 logs，不可写时退到系统临时目录。</summary>
    public static string ResolveLogRoot(string dataRoot)
    {
        var preferred = Path.Combine(dataRoot, "logs");
        if (TryPrepareWritable(preferred)) return preferred;

        var fallback = Path.Combine(Path.GetTempPath(), "ClipNest", "logs");
        if (TryPrepareWritable(fallback)) return fallback;

        return preferred;
    }

    /// <summary>创建目录并做一次真实写入探测。</summary>
    public static bool TryPrepareWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyFile(string source, string target)
    {
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target)) File.Copy(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
