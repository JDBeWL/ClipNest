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
            Assert.Equal(LanguageModes.System, settings.Settings.Language);
            Assert.Equal(FontPresets.System, settings.Settings.FontFamily);
            Assert.Equal(FontScales.Standard, settings.Settings.FontScale);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Language_Settings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Settings.Language = LanguageModes.JaJp;
            settings.Save();

            var reloaded = new SettingsService(path);
            Assert.Equal(LanguageModes.JaJp, reloaded.Settings.Language);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Typography_Settings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Settings.FontFamily = "DengXian";
            settings.Settings.FontScale = FontScales.Comfortable;
            settings.Save();

            var reloaded = new SettingsService(path);
            Assert.Equal("DengXian", reloaded.Settings.FontFamily);
            Assert.Equal(FontScales.Comfortable, reloaded.Settings.FontScale);
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
