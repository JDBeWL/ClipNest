using ClipNest.App.Services;

namespace ClipNest.App.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void TryPrepareWritable_CreatesAndCleansProbe()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"clipnest-paths-{Guid.NewGuid():N}");
        try
        {
            Assert.True(AppPaths.TryPrepareWritable(directory));
            Assert.True(Directory.Exists(directory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ResolveDataRoot_PrefersEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable("CLIPNEST_HOME");
        var custom = Path.Combine(Path.GetTempPath(), $"clipnest-home-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("CLIPNEST_HOME", custom);
            Assert.Equal(custom, AppPaths.ResolveDataRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPNEST_HOME", original);
        }
    }

    [Fact]
    public void ResolveDataRoot_UsesDataFolderNextToApplication()
    {
        var original = Environment.GetEnvironmentVariable("CLIPNEST_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CLIPNEST_HOME", null);
            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, "data"),
                AppPaths.ResolveDataRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPNEST_HOME", original);
        }
    }

    [Fact]
    public void ResolveLogRoot_CreatesLogsUnderDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-logs-{Guid.NewGuid():N}");
        try
        {
            var logs = AppPaths.ResolveLogRoot(root);
            Assert.Equal(Path.Combine(root, "logs"), logs);
            Assert.True(Directory.Exists(logs));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IsBuildOutput_RecognizesBinSegments()
    {
        Assert.True(AppPaths.IsBuildOutput(@"D:\repo\src\ClipNest.App\bin\Release\net10.0-windows"));
        Assert.True(AppPaths.IsBuildOutput(@"D:\repo\src\ClipNest.App\BIN\Debug\net10.0-windows"));
        Assert.False(AppPaths.IsBuildOutput(@"D:\Apps\ClipNest"));
    }

    [Fact]
    public void TryMigrateData_CopiesDatabaseSettingsAndCache()
    {
        var target = Path.Combine(Path.GetTempPath(), $"clipnest-target-{Guid.NewGuid():N}");
        var legacy = Path.Combine(Path.GetTempPath(), $"clipnest-legacy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "clipnest.db"), "db");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(legacy, "cache", "thumbs"));
            File.WriteAllText(Path.Combine(legacy, "cache", "thumbs", "a.jpg"), "thumb");

            AppPaths.TryMigrateData(target, legacy);

            Assert.Equal("db", File.ReadAllText(Path.Combine(target, "clipnest.db")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.Equal(
                "thumb",
                File.ReadAllText(Path.Combine(target, "cache", "thumbs", "a.jpg")));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
        }
    }

    [Fact]
    public void TryMigrateData_KeepsExistingTargetDatabase()
    {
        var target = Path.Combine(Path.GetTempPath(), $"clipnest-target-{Guid.NewGuid():N}");
        var legacy = Path.Combine(Path.GetTempPath(), $"clipnest-legacy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(target, "clipnest.db"), "new");
            File.WriteAllText(Path.Combine(legacy, "clipnest.db"), "old");

            AppPaths.TryMigrateData(target, legacy);

            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "clipnest.db")));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
        }
    }
}
