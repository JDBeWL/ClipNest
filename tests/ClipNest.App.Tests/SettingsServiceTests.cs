using ClipNest.App.Services;

namespace ClipNest.App.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Save_And_Load_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Settings.LastView = "Grid";
            settings.Save();

            var reloaded = new SettingsService(path);
            Assert.Equal("Grid", reloaded.Settings.LastView);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Missing_Ai_Toggles_Default_To_Enabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"LastView\":\"Grid\"}");

            var settings = new SettingsService(path);

            Assert.True(settings.Settings.AutoTaggingEnabled);
            Assert.True(settings.Settings.AutoIndexingEnabled);
            Assert.Equal(AppearanceModes.System, settings.Settings.Appearance);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Appearance_System_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Settings.Appearance = AppearanceModes.System;
            settings.Save();

            var reloaded = new SettingsService(path);
            Assert.Equal(AppearanceModes.System, reloaded.Settings.Appearance);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DataRoot_Is_Settings_Directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new SettingsService(path);
            Assert.Equal(directory, settings.DataRoot);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
