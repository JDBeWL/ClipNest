using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ClipNest.Ai.Tagging;
using ClipNest.App.Services;
using ClipNest.App.Theme;
using ClipNest.App.ViewModels;
using ClipNest.App.Views;
using ClipNest.Core.Tagging;

namespace ClipNest.App.Tests;

public sealed class AppLocalizationTests
{
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)(?:[^}]*)\}", RegexOptions.Compiled);

    [Fact]
    public void BuiltInLocales_HaveIdenticalKeys_NonEmptyValues_AndMatchingPlaceholders()
    {
        var baseTable = AppLocalization.GetTable(LanguageModes.ZhCn);
        Assert.NotEmpty(baseTable);

        foreach (var locale in new[] { LanguageModes.ZhCn, LanguageModes.EnUs, LanguageModes.JaJp })
        {
            Assert.True(AppLocalization.HasLocale(locale), $"缺少内置语言包：{locale}");
            var table = AppLocalization.GetTable(locale);

            var missing = baseTable.Keys.Except(table.Keys, StringComparer.Ordinal).ToList();
            var extra = table.Keys.Except(baseTable.Keys, StringComparer.Ordinal).ToList();
            Assert.Empty(missing);
            Assert.Empty(extra);

            foreach (var (key, value) in table)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"语言包 {locale} 的键 {key} 为空");

                var expectedPlaceholders = ExtractPlaceholderIndices(baseTable[key]);
                var actualPlaceholders = ExtractPlaceholderIndices(value);
                Assert.Equal(expectedPlaceholders, actualPlaceholders);
            }
        }
    }

    [Fact]
    public void LanguageModes_ResolvesSystemCulturesCorrectly()
    {
        Assert.Equal(LanguageModes.ZhCn, LanguageModes.Resolve(LanguageModes.System, CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal(LanguageModes.ZhCn, LanguageModes.Resolve(LanguageModes.System, CultureInfo.GetCultureInfo("zh-TW")));
        Assert.Equal(LanguageModes.JaJp, LanguageModes.Resolve(LanguageModes.System, CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal(LanguageModes.EnUs, LanguageModes.Resolve(LanguageModes.System, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(LanguageModes.EnUs, LanguageModes.Resolve(LanguageModes.System, CultureInfo.GetCultureInfo("fr-FR")));

        Assert.Equal(LanguageModes.System, LanguageModes.Normalize(null));
        Assert.Equal(LanguageModes.System, LanguageModes.Normalize(""));
        Assert.Equal(LanguageModes.ZhCn, LanguageModes.Normalize("zh"));
        Assert.Equal(LanguageModes.EnUs, LanguageModes.Normalize("en-GB"));
        Assert.Equal(LanguageModes.JaJp, LanguageModes.Normalize("ja"));
        Assert.Equal(LanguageModes.System, LanguageModes.Normalize("unknown-xx"));
    }

    [AvaloniaFact]
    public void FormatCount_AndJapaneseTerminology_MatchExpectedCopy()
    {
        var previousSelection = AppLocalization.CurrentSelection;
        try
        {
            AppLocalization.Apply(LanguageModes.EnUs);
            Assert.Equal("1 image", AppLocalization.FormatCount("Status.ImageCountOne", "Status.ImageCount", 1));
            Assert.Equal("5 images", AppLocalization.FormatCount("Status.ImageCountOne", "Status.ImageCount", 5));
            Assert.Equal("Moving 1 image…", AppLocalization.FormatCount("Status.MovingOne", "Status.Moving", 1));
            Assert.Equal("Moving 3 images…", AppLocalization.FormatCount("Status.MovingOne", "Status.Moving", 3));
            Assert.Equal("Folder added (1 new file)", AppLocalization.FormatCount("Status.FolderAddedOne", "Status.FolderAdded", 1));
            Assert.Equal("Folder added (4 new files)", AppLocalization.FormatCount("Status.FolderAddedOne", "Status.FolderAdded", 4));
            Assert.Equal("Copied 1 file to clipboard", AppLocalization.FormatCount("Status.FilesCopiedOne", "Status.FilesCopied", 1));
            Assert.Equal("Copied 2 files to clipboard", AppLocalization.FormatCount("Status.FilesCopiedOne", "Status.FilesCopied", 2));

            AppLocalization.Apply(LanguageModes.JaJp);
            Assert.Equal("アンインストール", AppLocalization.Get("Common.Uninstall"));
            Assert.Equal("デフォルト", AppLocalization.Get("Common.DefaultBadge"));
            Assert.Equal("デフォルトに設定", AppLocalization.Get("Common.SetDefault"));
            Assert.Equal("デフォルトに従う", AppLocalization.Get("Common.FollowDefault"));
        }
        finally
        {
            AppLocalization.Apply(previousSelection);
        }
    }

    [AvaloniaFact]
    public void ApplyLanguage_HotSwitchesResources_ViewModel_And_OpenWindows()
    {
        var previousSelection = AppLocalization.CurrentSelection;
        var previousFont = AppFonts.CurrentSelection;
        var previousScale = AppFonts.CurrentScale;
        var settingsPath = Path.Combine(Path.GetTempPath(), $"clipnest-loc-{Guid.NewGuid():N}.json");
        var tempModelRoot = Path.Combine(Path.GetTempPath(), $"clipnest-loc-models-{Guid.NewGuid():N}");

        try
        {
            var settings = new SettingsService(settingsPath);
            var catalog = new TagModelCatalog(TagModelDefinition.All
                .Select(def => new TagModelEntry(
                    def,
                    new TagModelManager(Path.Combine(tempModelRoot, def.Id), def),
                    new DummyTagger()))
                .ToList());

            var vm = new MainWindowViewModel(settings: settings, tagCatalog: catalog);
            var window = new MainWindow(vm, new DummyThumbnails(), settings);
            window.Show();
            try
            {
                vm.OpenSettingsCommand.Execute(null);
                window.UpdateLayout();

                var settingsPage = window.FindControl<SettingsView>("SettingsPage")!;
                var backBtn = settingsPage.FindControl<Button>("BackButton")!;
                var backText = backBtn.GetVisualDescendants().OfType<TextBlock>().First();
                var languageCombo = settingsPage.FindControl<ComboBox>("LanguageComboBox")!;
                var fontCombo = settingsPage.FindControl<ComboBox>("FontFamilyComboBox")!;

                // 通过 ComboBox 自身切换英文，验证选中项与字体下拉框不会变空且文案同步更新。
                var english = vm.AvailableLanguages.First(c => c.Id == LanguageModes.EnUs);
                languageCombo.SelectedItem = english;
                window.UpdateLayout();

                Assert.Same(english, languageCombo.SelectedItem);
                Assert.Same(english, languageCombo.SelectionBoxItem);
                Assert.NotNull(fontCombo.SelectedItem);
                Assert.Equal("Recommended (System UI)", Assert.IsType<FontChoice>(fontCombo.SelectedItem).DisplayName);
                Assert.Equal("Follow System", vm.AvailableLanguages.First(c => c.Id == LanguageModes.System).DisplayName);

                Assert.Equal(LanguageModes.EnUs, settings.Settings.Language);
                Assert.Equal("Overview", vm.InspectorHeaderTitle);
                Assert.Equal("All Images", vm.CollectionTitle);
                Assert.Equal("Default", vm.SortLabel);
                Assert.Equal("RAM++ General", vm.TagModels.First(m => m.Id == "ram-plus-plus").DisplayName);

                var app = Application.Current!;
                Assert.True(app.Resources.TryGetResource("Str.Settings.Title", null, out var enTitle));
                Assert.Equal("Settings", enTitle);
                Assert.Equal("Back to Library", backText.Text);
                Assert.Equal("Back to Library (Esc)", ToolTip.GetTip(backBtn));

                // 切到日文，验证日文文案与默认系统字体链优先日文字体。
                var japanese = vm.AvailableLanguages.First(c => c.Id == LanguageModes.JaJp);
                languageCombo.SelectedItem = japanese;
                window.UpdateLayout();

                Assert.Same(japanese, languageCombo.SelectedItem);
                Assert.Same(japanese, languageCombo.SelectionBoxItem);
                Assert.Equal("おすすめ（システム標準）", Assert.IsType<FontChoice>(fontCombo.SelectedItem).DisplayName);
                Assert.Equal("システムに従う", vm.AvailableLanguages.First(c => c.Id == LanguageModes.System).DisplayName);
                Assert.Equal(LanguageModes.JaJp, settings.Settings.Language);
                Assert.Equal("ライブラリ概要", vm.InspectorHeaderTitle);
                Assert.Equal("すべての画像", vm.CollectionTitle);
                Assert.Equal("標準順", vm.SortLabel);
                Assert.Contains("Yu Gothic UI", AppFonts.Ui.ToString());
                Assert.Equal("ライブラリへ戻る", backText.Text);
                Assert.Equal("ライブラリへ戻る (Esc)", ToolTip.GetTip(backBtn));

                // 切回简体中文，验证全部文案无残留。
                var chinese = vm.AvailableLanguages.First(c => c.Id == LanguageModes.ZhCn);
                languageCombo.SelectedItem = chinese;
                window.UpdateLayout();

                Assert.Same(chinese, languageCombo.SelectedItem);
                Assert.Same(chinese, languageCombo.SelectionBoxItem);
                Assert.Equal("推荐默认（跟随系统）", Assert.IsType<FontChoice>(fontCombo.SelectedItem).DisplayName);
                Assert.Equal("图库概览", vm.InspectorHeaderTitle);
                Assert.Equal("全部图片", vm.CollectionTitle);
                Assert.Equal("返回图库", backText.Text);
                Assert.Equal("返回图库 (Esc)", ToolTip.GetTip(backBtn));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            AppLocalization.Apply(previousSelection);
            AppFonts.Apply(previousFont, previousScale);
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private static int[] ExtractPlaceholderIndices(string text) =>
        PlaceholderPattern.Matches(text)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(index => index)
            .ToArray();

    private sealed class DummyTagger : ITagger
    {
        public bool IsAvailable => false;
        public Task<AiTagResult> TagAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(AiTagResult.Empty);
        public Task ReleaseIfIdleAsync(TimeSpan idleFor, CancellationToken ct = default) =>
            Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class DummyThumbnails : IThumbnailProvider
    {
        public Avalonia.Media.Imaging.Bitmap? GetCached(string sha256) => null;
        public Task<Avalonia.Media.Imaging.Bitmap?> GetAsync(
            ClipNest.Core.Models.AssetListItem item, int maxSize, CancellationToken ct = default) =>
            Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null);
        public void ReleaseCached(string sha256) { }
    }
}
