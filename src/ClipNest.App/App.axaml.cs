using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipNest.App.Theme;
using ClipNest.Ai;
using ClipNest.Ai.Acceleration;
using ClipNest.Ai.Models;
using ClipNest.Ai.Characters;
using ClipNest.Ai.Tagging;
using ClipNest.App.Services;
using ClipNest.App.ViewModels;
using ClipNest.App.Views;
using ClipNest.Core.Embedding;
using ClipNest.Core.Indexing;
using ClipNest.Core.Tagging;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using ClipNest.Data.Search;
using ClipNest.Imaging.Decoding;
using ClipNest.Imaging.Indexing;
using ClipNest.Imaging.Metadata;
using ClipNest.Imaging.Thumbnails;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ClipNest.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private string _appearanceMode = AppearanceModes.System;
    private bool _watchingPlatformTheme;

    /// <summary>切换外观并同步窗框。设置由调用方负责持久化。</summary>
    public void ApplyAppearance(string mode)
    {
        _appearanceMode = AppearanceModes.Normalize(mode);
        var variant = ResolveThemeVariant(_appearanceMode);
        RequestedThemeVariant = variant;
        ThemeColors.Apply(variant);
        RefreshWindowChrome();
    }

    /// <summary>
    /// 把外观模式解析成明确的深浅变体。自绘控件只认 ThemeColors.IsLight，
    /// 跟随系统时必须在入口处落到 Light 或 Dark，不能让 Default 流进去。
    /// </summary>
    private static ThemeVariant ResolveThemeVariant(string mode)
    {
        if (mode == AppearanceModes.Light) return ThemeVariant.Light;
        if (mode != AppearanceModes.System) return ThemeVariant.Dark;

        var platform = Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        return platform == PlatformThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private void RefreshWindowChrome()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        foreach (var window in desktop.Windows)
        {
            WindowChrome.ApplyTitleBar(window);

            // 自绘控件缓存了画刷，资源切换不会自动让它们重绘。
            InvalidateTree(window);

            // 工具栏的太阳与月亮图标跟着实际主题走，跟随系统时也能同步。
            if (window.DataContext is MainWindowViewModel vm) vm.NotifyAppearanceChanged();
        }
    }

    /// <summary>让整棵树重绘一次。只在主题切换时调用，成本可忽略。</summary>
    private static void InvalidateTree(Visual visual)
    {
        visual.InvalidateVisual();
        foreach (var child in visual.GetVisualChildren())
        {
            if (child is Visual nested) InvalidateTree(nested);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = AppPaths.ResolveDataRoot();

            ClipNestDatabase db;
            try
            {
                Directory.CreateDirectory(root);
                AppPaths.TryMigrateLegacyData(root);
                db = new ClipNestDatabase(Path.Combine(root, "clipnest.db"));
                db.InitializeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Data directory initialization failed");
                desktop.MainWindow = new ErrorWindow(
                    "ClipNest 无法创建或写入数据库。常见原因是安全软件（如火绒、Defender 受控文件夹访问）"
                    + "阻止了写入，或目录权限不足。\n\n"
                    + "请将 ClipNest 加入安全软件信任区，或设置环境变量 CLIPNEST_HOME 指向可写目录，"
                    + "然后重新启动。",
                    root);
                return;
            }

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<FolderRepository>();
            services.AddSingleton<GalaxyRepository>();
            services.AddSingleton<AssetRepository>();
            services.AddSingleton<AssetFileOperations>();
            services.AddSingleton<TagRepository>();
            services.AddSingleton<ThumbnailRepository>();
            services.AddSingleton<SearchRepository>();
            services.AddSingleton<AssetSearchService>();
            services.AddSingleton<IAssetSearchService>(sp => sp.GetRequiredService<AssetSearchService>());
            services.AddSingleton<IFileMetadataReader, ExifMetadataReader>();
            services.AddSingleton<IImageDecoder, SkiaImageDecoder>();
            services.AddSingleton<IImageDecoder, WicImageDecoder>();
            services.AddSingleton<IImageDecoder, LibRawImageDecoder>();
            services.AddSingleton<IAiImageSource, AiImageSource>();
            services.AddSingleton(new ThumbnailCache(Path.Combine(root, "cache", "thumbs")));
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton<IThumbnailProvider, ThumbnailProvider>();
            services.AddSingleton<FileScanner>();
            var settingsService = new SettingsService(Path.Combine(root, "settings.json"));
            services.AddSingleton(settingsService);

            // 线程数必须在任何推理会话创建之前确定。
            OrtAccelerator.IntraOpThreads = settingsService.Settings.InferenceThreads;

            // 必须在任何窗口创建之前定好变体。自绘控件在构造时就会读取色板。
            _appearanceMode = AppearanceModes.Normalize(settingsService.Settings.Appearance);
            var appearanceVariant = ResolveThemeVariant(_appearanceMode);
            RequestedThemeVariant = appearanceVariant;
            ThemeColors.Apply(appearanceVariant);

            // 跟随系统时订阅平台主题变化；只在该模式下重新解析，避免覆盖手动选择。
            if (!_watchingPlatformTheme && PlatformSettings is { } platformSettings)
            {
                _watchingPlatformTheme = true;
                platformSettings.ColorValuesChanged += (_, _) =>
                {
                    if (_appearanceMode != AppearanceModes.System) return;
                    Dispatcher.UIThread.Post(() => ApplyAppearance(AppearanceModes.System));
                };
            }

            // 语义模型与后台嵌入队列。
            var modelRoot = Path.Combine(root, "models", ClipModelDefinition.ChineseClipVitB16.Id);
            services.AddSingleton(new ClipModelManager(modelRoot));
            services.AddSingleton(ClipModelDefinition.ChineseClipVitB16);
            services.AddSingleton<IClipEncoder, ClipEncoder>();
            services.AddSingleton<EmbeddingRepository>();
            services.AddSingleton<EmbeddingQueue>();
            services.AddSingleton(sp => new SemanticSearchService(
                sp.GetRequiredService<EmbeddingRepository>(),
                sp.GetRequiredService<SearchRepository>(),
                sp.GetRequiredService<IClipEncoder>(),
                ClipModelDefinition.ChineseClipVitB16.Id));

            // 标注模型与后台标注队列。权重按需下载，未安装的模型由队列跳过。
            var tagRoot = Path.Combine(root, "models");
            var siglipAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "siglip2");
            services.AddSingleton(sp =>
            {
                var images = sp.GetRequiredService<IAiImageSource>();
                var entries = TagModelDefinition.All
                    .Select(definition =>
                    {
                        var manager = new TagModelManager(
                            Path.Combine(tagRoot, definition.Id), definition);
                        ITagger tagger = definition.Kind switch
                        {
                            TagModelKind.Camie => new CamieTagger(manager, images),
                            TagModelKind.RecognizeAnything => new RamTagger(manager, images),
                            TagModelKind.Siglip => new SiglipTagger(manager, images, siglipAssets),
                            _ => throw new InvalidOperationException(
                                $"未知标注模型类型：{definition.Kind}")
                        };
                        return new TagModelEntry(definition, manager, tagger);
                    })
                    .ToList();
                return new TagModelCatalog(entries);
            });
            services.AddSingleton<AiTagRepository>();
            services.AddSingleton(sp => new TaggingQueue(
                sp.GetRequiredService<AiTagRepository>(),
                sp.GetRequiredService<TagModelCatalog>(),
                () => settingsService.Settings.TagModelId));

            // 角色归属与原型。身份特征复用语义向量，不额外下载模型。
            services.AddSingleton<CharacterRepository>();
            services.AddSingleton(sp => new CharacterService(sp.GetRequiredService<CharacterRepository>()));

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();

            var provider = services.BuildServiceProvider();

            // 首轮扫描前同步暂停状态，关闭自动处理的用户不会在启动时被抢跑。
            var embeddingQueue = provider.GetRequiredService<EmbeddingQueue>();
            embeddingQueue.IsPaused = !settingsService.Settings.AutoIndexingEnabled;
            embeddingQueue.Start();

            var taggingQueue = provider.GetRequiredService<TaggingQueue>();
            taggingQueue.IsPaused = !settingsService.Settings.AutoTaggingEnabled;
            taggingQueue.Start();

            desktop.MainWindow = provider.GetRequiredService<MainWindow>();
        }

        ActualThemeVariantChanged += (_, _) =>
        {
            ThemeColors.Apply(ActualThemeVariant);
            RefreshWindowChrome();
        };

        base.OnFrameworkInitializationCompleted();
    }
}
