using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using ClipNest.App.Services;
using ClipNest.App.Theme;
using ClipNest.App.ViewModels;

namespace ClipNest.App.Tests;

public sealed class AppFontsTests
{
    [AvaloniaFact]
    public void SystemAndCustomFonts_UseDirectSingleFamilyWithoutCompositeOrEmbeddedUri()
    {
        var previousCulture = AppLocalization.CurrentCultureCode;
        try
        {
            AppLocalization.Apply(LanguageModes.ZhCn);
            AppFonts.Apply(FontPresets.System, FontScales.Standard);
            Assert.Null(AppFonts.Ui.Key);
            Assert.Null(AppFonts.Display.Key);
            Assert.Single(AppFonts.Ui.FamilyNames);
            Assert.DoesNotContain("Noto Sans SC", AppFonts.Ui.FamilyNames);

            AppLocalization.Apply(LanguageModes.JaJp);
            AppFonts.Apply(FontPresets.System, FontScales.Standard);
            Assert.Null(AppFonts.Ui.Key);
            Assert.Equal("Yu Gothic UI", AppFonts.Ui.Name);

            AppLocalization.Apply(LanguageModes.EnUs);
            AppFonts.Apply(FontPresets.System, FontScales.Standard);
            Assert.Null(AppFonts.Ui.Key);
            Assert.Equal("Segoe UI", AppFonts.Ui.Name);

            // 本地化中文字体名必须构造单一 FontFamily，不能落入 compositefont 导致 Skia 跳过匹配。
            foreach (var localized in new[] { "寒蝉半圆体", "大波浪圓體 CJK TC", "微软雅黑", "DengXian" })
            {
                AppFonts.Apply(localized, FontScales.Standard);
                Assert.Null(AppFonts.Ui.Key);
                Assert.Single(AppFonts.Ui.FamilyNames);
                Assert.Equal(localized, AppFonts.Ui.Name);
            }

            AppFonts.Apply(FontPresets.Bundled, FontScales.Standard);
            Assert.NotNull(AppFonts.Ui.Key);
            Assert.Equal("avares://ClipNest.App/Assets/Fonts", AppFonts.Ui.Key!.Source.ToString());
            Assert.Equal(AppFonts.BundledNotoUri, AppFonts.Ui.ToString());
        }
        finally
        {
            AppLocalization.Apply(previousCulture);
            AppFonts.Apply(FontPresets.System, FontScales.Standard);
        }
    }

    [Fact]
    public void FontPresets_MapsSystemNotoSansScToBundledStatic()
    {
        // 系统里的 Noto Sans SC 可变字体只暴露 Thin 字重，统一转到内置静态字体。
        Assert.Equal(FontPresets.Bundled, FontPresets.Normalize("Noto Sans SC"));
        Assert.Equal(FontPresets.Bundled, FontPresets.Normalize("  noto sans sc  "));
        Assert.Equal(FontPresets.System, FontPresets.Normalize(""));
        Assert.Equal(FontPresets.System, FontPresets.Normalize(null));
        Assert.Equal("MiSans", FontPresets.Normalize(" MiSans "));
    }

    [Fact]
    public void BuildUiFamily_UsesDirectFontNameSoLocalizedFontsMatchSkia()
    {
        var family = AppFonts.BuildUiFamily("寒蝉半圆体");
        var names = family.FamilyNames.ToList();

        Assert.Null(family.Key);
        Assert.Single(names);
        Assert.Equal("寒蝉半圆体", names[0]);
    }

    [Fact]
    public void BuildChoices_FiltersVerticalAndSymbolFontsMatchesLocalizedCuratedAndKeepsMissingSelection()
    {
        var choices = AppFonts.BuildChoices(
            new[]
            {
                "@Microsoft YaHei",
                "Wingdings",
                "Segoe MDL2 Assets",
                "Noto Sans SC",
                "等线",
                "微软雅黑",
                "Microsoft YaHei UI",
                "寒蝉半圆体",
                "大波浪圓體 CJK TC",
                "Consolas",
            },
            "MyCustomFont");

        Assert.Equal(FontPresets.System, choices[0].Id);
        Assert.Equal(FontPresets.Bundled, choices[1].Id);
        Assert.Equal("Microsoft YaHei UI", choices[2].Id);
        Assert.Equal("微软雅黑", choices[3].Id);
        Assert.Equal("等线", choices[4].Id);
        Assert.DoesNotContain(choices, c => c.Id.StartsWith('@'));
        Assert.DoesNotContain(choices, c => c.Id == "Wingdings");
        Assert.DoesNotContain(choices, c => c.Id == "Segoe MDL2 Assets");
        Assert.DoesNotContain(choices, c => c.Id == "Noto Sans SC");
        Assert.Contains(choices, c => c.Id == "寒蝉半圆体" && c.PreviewFontFamily.Key is null);
        Assert.Contains(choices, c => c.Id == "大波浪圓體 CJK TC" && c.PreviewFontFamily.Key is null);
        Assert.Contains(choices, c => c.Id == "Consolas");
        Assert.Contains(choices, c => c.Id == "MyCustomFont" && c.DisplayName.Contains("未安装"));
    }

    [AvaloniaFact]
    public void Apply_UpdatesDynamicResourcesAndBumpsVersion()
    {
        try
        {
            var before = AppFonts.Version;
            AppFonts.Apply("DengXian", FontScales.Comfortable);

            Assert.True(AppFonts.Version > before);
            Assert.Equal("DengXian", AppFonts.CurrentSelection);
            Assert.Equal(FontScales.Comfortable, AppFonts.CurrentScale);

            var app = Application.Current!;
            Assert.True(app.Resources.TryGetResource("Font.UI", null, out var uiFont));
            Assert.Equal("DengXian", Assert.IsType<FontFamily>(uiFont).FamilyNames[0]);
            Assert.True(app.Resources.TryGetResource("FontSize.Body", null, out var bodySize));
            Assert.Equal(14.5, Assert.IsType<double>(bodySize));
        }
        finally
        {
            AppFonts.Apply(FontPresets.System, FontScales.Standard);
        }
    }

    [AvaloniaFact]
    public void ViewModel_SwitchesFontAndScale_AndResetsToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            var vm = new MainWindowViewModel(settings: settings);

            Assert.False(vm.CanResetTypography);
            Assert.NotEmpty(vm.AvailableFonts);
            Assert.Equal(FontPresets.System, vm.SelectedFontChoice?.Id);

            var bundled = vm.AvailableFonts.First(c => c.Id == FontPresets.Bundled);
            vm.SelectedFontChoice = bundled;
            vm.SelectFontScaleCommand.Execute(FontScales.Comfortable);

            Assert.True(vm.CanResetTypography);
            Assert.Equal(FontPresets.Bundled, settings.Settings.FontFamily);
            Assert.Equal(FontScales.Comfortable, settings.Settings.FontScale);
            Assert.True(vm.IsFontScaleComfortable);

            vm.ResetTypographyCommand.Execute(null);

            Assert.False(vm.CanResetTypography);
            Assert.Equal(FontPresets.System, settings.Settings.FontFamily);
            Assert.Equal(FontScales.Standard, settings.Settings.FontScale);
            Assert.Equal(FontPresets.System, vm.SelectedFontChoice?.Id);
        }
        finally
        {
            AppFonts.Apply(FontPresets.System, FontScales.Standard);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
