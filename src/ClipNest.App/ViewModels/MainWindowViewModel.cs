using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClipNest.Ai;
using ClipNest.Ai.Acceleration;
using ClipNest.Ai.Models;
using ClipNest.Ai.Tagging;
using ClipNest.App.Controls;
using ClipNest.App.Services;
using ClipNest.App.Theme;
using ClipNest.Core.Embedding;
using ClipNest.Core.Models;
using ClipNest.Core.Tagging;
using ClipNest.Data.Repositories;
using ClipNest.Data.Search;
using ClipNest.Imaging.Indexing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClipNest.App.ViewModels;

public enum LibraryCollection
{
    All,
    Untagged,
    Folder,
    Search
}

/// <summary>设置页左侧导航的分区。</summary>
public enum SettingsPageSection
{
    Appearance,
    Browsing,
    Ai,
    Data
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IAssetSearchService? _search;
    private readonly TagRepository? _tags;
    private readonly FileScanner? _scanner;
    private readonly SettingsService? _settings;
    private readonly FolderRepository? _folders;
    private readonly IThumbnailProvider? _thumbnails;
    private readonly GalaxyRepository? _galaxy;
    private readonly ClipModelManager? _models;
    private readonly IClipEncoder? _clipEncoder;
    private readonly EmbeddingQueue? _embeddingQueue;
    private readonly TaggingQueue? _tagQueue;
    private readonly TagModelCatalog? _tagCatalog;
    private readonly AssetFileOperations? _operations;
    private TagModelOption? _followDefaultModel;

    /// <summary>图库集合不设条数上限，条数由数据库里的实际图片数决定。</summary>
    private const int NoLimit = int.MaxValue;

    /// <summary>上一次检索是否走了语义重排；决定结果是否按名次呈现。</summary>
    private bool _lastSearchWasSemantic;
    private bool _suppressFolderFilter;
    private int _collectionToken;
    private int _lastPending;
    private int _lastTagPending;

    /// <summary>队列排空后要写回状态栏的文案；为空时不改动用户当前的状态。</summary>
    private string? _completionStatusText;
    private readonly Dictionary<string, CancellationTokenSource> _tagModelDownloads =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _semanticModelDownload;
    private DispatcherTimer? _settingsSaveTimer;
    private IReadOnlyList<AssetListItem> _selectedItems = Array.Empty<AssetListItem>();
    private IReadOnlyList<AssetListItem> _baseItems = Array.Empty<AssetListItem>();

    [ObservableProperty] private ViewMode _currentView = ViewMode.Grid;
    [ObservableProperty] private LibraryCollection _activeCollection = LibraryCollection.All;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchQueryLabel = "";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _runtimeWarning = "";
    [ObservableProperty] private AssetListItem? _selectedItem;
    [ObservableProperty] private string _newTag = "";
    [ObservableProperty] private Folder? _selectedFolder;
    [ObservableProperty] private Bitmap? _previewBitmap;
    [ObservableProperty] private Bitmap? _environmentBitmap;
    [ObservableProperty] private IReadOnlySet<long>? _highlightedIds;
    [ObservableProperty] private bool _semanticEnabled;
    [ObservableProperty] private string _semanticStatus = "语义模型未安装";
    [ObservableProperty] private bool _isModelReady;
    [ObservableProperty] private bool _isModelDownloading;
    [ObservableProperty] private double _modelDownloadProgress;
    [ObservableProperty] private bool _isInspectorOpen = true;
    [ObservableProperty] private double _thumbnailSize = 200;
    [ObservableProperty] private string _sortMode = "default";
    [ObservableProperty] private int _selectionCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _tagModelStatus = "";
    [ObservableProperty] private string _defaultTagModelId = "";
    [ObservableProperty] private bool _isTagModelReady;
    [ObservableProperty] private bool _isTagModelDownloading;
    /// <summary>推理设备文案，随会话创建刷新；DirectML 不可用时显示 CPU。</summary>
    [ObservableProperty] private string _accelerationStatus = "";
    /// <summary>推理线程数；识别在后台运行，线程越多占用 CPU 越多。</summary>
    [ObservableProperty] private decimal? _inferenceThreads;
    /// <summary>识别进度（0..1），仅用于设置页的细进度条。</summary>
    [ObservableProperty] private double _tagProgress;
    /// <summary>是否有待识别的图片；为 false 时隐藏进度条。</summary>
    [ObservableProperty] private bool _isTagging;
    [ObservableProperty] private bool _isReidentifying;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private SettingsPageSection _settingsSection = SettingsPageSection.Appearance;
    [ObservableProperty] private string _appearanceMode = AppearanceModes.System;
    [ObservableProperty] private bool _autoTaggingEnabled = true;
    [ObservableProperty] private bool _autoIndexingEnabled = true;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private int _importProcessed;
    [ObservableProperty] private int _importTotal;

    public string ImportText => ImportTotal > 0
        ? $"导入中 {ImportProcessed}/{ImportTotal}"
        : "正在扫描文件夹…";

    public double ImportFraction => ImportTotal > 0 ? (double)ImportProcessed / ImportTotal : 0;
    public bool IsImportIndeterminate => ImportTotal <= 0;

    public bool IsAppearanceSection => SettingsSection == SettingsPageSection.Appearance;
    public bool IsBrowsingSection => SettingsSection == SettingsPageSection.Browsing;
    public bool IsAiSection => SettingsSection == SettingsPageSection.Ai;
    public bool IsDataSection => SettingsSection == SettingsPageSection.Data;

    /// <summary>当前是否处于浅色外观，由实际主题决定，只用于工具栏图标。</summary>
    public bool IsLightAppearance => ThemeColors.IsLight;

    public bool IsAppearanceDark => AppearanceMode == AppearanceModes.Dark;
    public bool IsAppearanceLight => AppearanceMode == AppearanceModes.Light;
    public bool IsAppearanceSystem => AppearanceMode == AppearanceModes.System;
    public bool HasRuntimeWarning => RuntimeWarning.Length > 0;
    public bool HasReadyTagModel => TagModels.Any(model => model.IsReady);
    public bool CanReidentifyTags => HasReadyTagModel && !IsReidentifying;
    public bool HasFolderTagModels => FolderTagModels.Count > 0;

    /// <summary>线程数上限，取当前机器的逻辑核心数。</summary>
    public decimal MaxInferenceThreads => Environment.ProcessorCount;

    /// <summary>数据目录，用于设置页展示与打开。</summary>
    public string DataDirectory { get; }

    /// <summary>程序版本，设置页展示用；去掉信息版本里的 +git 提交后缀。</summary>
    public string AppVersion { get; } = ResolveAppVersion();

    private static string ResolveAppVersion()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var separator = informational.IndexOf('+');
            return separator > 0 ? informational[..separator] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public BulkObservableCollection<AssetListItem> Items { get; } = new();
    public ObservableCollection<TagTreeItemViewModel> Tags { get; } = new();
    public ObservableCollection<Tag> SelectedTags { get; } = new();

    /// <summary>详情栏单独展示的角色。AI 结论与手动编辑共用同一份类别标签。</summary>
    public ObservableCollection<Tag> SelectedCharacters { get; } = new();

    [ObservableProperty] private string _selectedSeriesText = "";
    [ObservableProperty] private bool _isEditingCharacters;
    [ObservableProperty] private string _characterDraft = "";
    [ObservableProperty] private bool _isEditingSeries;
    [ObservableProperty] private string _seriesDraft = "";
    public ObservableCollection<Folder> Folders { get; } = new();
    public ObservableCollection<TagModelOption> TagModels { get; } = new();
    public ObservableCollection<FolderTagModelOption> FolderTagModels { get; } = new();
    public ObservableCollection<GalaxyCluster> GalaxyClusters { get; } = new();
    public ObservableCollection<GalaxyNode> GalaxyNodes { get; } = new();

    public MainWindowViewModel(
        IAssetSearchService? search = null,
        TagRepository? tags = null,
        FileScanner? scanner = null,
        SettingsService? settings = null,
        FolderRepository? folders = null,
        IThumbnailProvider? thumbnails = null,
        GalaxyRepository? galaxy = null,
        ClipModelManager? models = null,
        EmbeddingQueue? embeddingQueue = null,
        TaggingQueue? taggingQueue = null,
        TagModelCatalog? tagCatalog = null,
        AssetFileOperations? operations = null,
        IClipEncoder? clipEncoder = null)
    {
        _search = search;
        _tags = tags;
        _scanner = scanner;
        _settings = settings;
        _folders = folders;
        _thumbnails = thumbnails;
        _galaxy = galaxy;
        _models = models;
        _embeddingQueue = embeddingQueue;
        _tagQueue = taggingQueue;
        _tagCatalog = tagCatalog;
        _operations = operations;
        _clipEncoder = clipEncoder;

        BuildTagModelOptions(settings?.Settings.TagModelId);

        Folders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFolders));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyDescription));
        };
        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTags));
        FolderTagModels.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasFolderTagModels));
        SelectedCharacters.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSelectedCharacters));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        if (settings is not null)
        {
            // 界面版本低于 3 时统一迁移到图库瀑布流，避免用户停留在旧视图。
            if (settings.Settings.UiVersion < 3)
            {
                CurrentView = ViewMode.Grid;
                settings.Settings.LastView = nameof(ViewMode.Grid);
                settings.Settings.UiVersion = 3;
                settings.Save();
            }
            else if (Enum.TryParse<ViewMode>(settings.Settings.LastView, out var savedView))
            {
                CurrentView = savedView;
            }

            // 直接写入后备字段，避免构造阶段触发保存。
            _isInspectorOpen = settings.Settings.InspectorOpen;
            _thumbnailSize = Math.Clamp(settings.Settings.ThumbnailSize, 132, 340);
            _sortMode = NormalizeSortMode(settings.Settings.SortMode);
            _inferenceThreads = ResolveInferenceThreads(settings.Settings.InferenceThreads);
        }

        _semanticEnabled = settings?.Settings.SemanticEnabled ?? false;
        IsModelReady = models?.IsReady ?? false;

        if (embeddingQueue is not null)
            embeddingQueue.ProgressChanged += OnEmbeddingProgressChanged;

        if (taggingQueue is not null)
            taggingQueue.ProgressChanged += OnTaggingProgressChanged;

        // 直接写字段，避免构造期触发切换逻辑，因为此时窗口还没建好。
        _appearanceMode = AppearanceModes.Normalize(settings?.Settings.Appearance);
        _autoTaggingEnabled = settings?.Settings.AutoTaggingEnabled ?? true;
        _autoIndexingEnabled = settings?.Settings.AutoIndexingEnabled ?? true;
        DataDirectory = settings?.DataRoot ?? "";

        // 只检查文件，不触发 ONNX 加载；缺少运行库时标注任务逐张失败，由队列继续轮询。
        if (!VcRuntimeProbe.IsAvailable())
        {
            _runtimeWarning =
                "AI 标注与语义搜索需要 Microsoft Visual C++ 运行库（x64）。"
                + "浏览、导入与整理不受影响，安装后重启 ClipNest 即可启用 AI。";
        }

        _ = RefreshSemanticStatusAsync();
        _ = RefreshTagModelStatusAsync();
    }

    partial void OnAppearanceModeChanged(string value)
    {
        if (_settings is not null)
        {
            _settings.Settings.Appearance = value;
            _settings.Save();
        }

        if (Avalonia.Application.Current is App app) app.ApplyAppearance(value);

        OnPropertyChanged(nameof(IsLightAppearance));
        OnPropertyChanged(nameof(IsAppearanceDark));
        OnPropertyChanged(nameof(IsAppearanceLight));
        OnPropertyChanged(nameof(IsAppearanceSystem));
    }

    partial void OnRuntimeWarningChanged(string value) =>
        OnPropertyChanged(nameof(HasRuntimeWarning));

    partial void OnDefaultTagModelIdChanged(string value) => RefreshTagModelRows();

    partial void OnIsTagModelReadyChanged(bool value) =>
        OnPropertyChanged(nameof(CanReidentifyTags));

    partial void OnIsReidentifyingChanged(bool value) =>
        OnPropertyChanged(nameof(CanReidentifyTags));

    /// <summary>0 表示自动，按核心数折半，与运行时的默认值一致。</summary>
    private static decimal ResolveInferenceThreads(int configured) =>
        configured > 0
            ? Math.Clamp(configured, 1, Environment.ProcessorCount)
            : Math.Max(1, Environment.ProcessorCount / 2);

    partial void OnInferenceThreadsChanged(decimal? value)
    {
        if (value is null) return;

        var threads = Math.Clamp((int)value.Value, 1, Environment.ProcessorCount);
        OrtAccelerator.IntraOpThreads = threads;

        if (_settings is not null)
        {
            _settings.Settings.InferenceThreads = threads;
            _settings.Save();
        }

        // 已有会话按旧线程数创建，释放后由下一张识别按新设置重建。
        _ = _tagQueue?.ReleaseSessionsAsync();
    }

    partial void OnAutoTaggingEnabledChanged(bool value)
    {
        if (_settings is not null)
        {
            _settings.Settings.AutoTaggingEnabled = value;
            _settings.Save();
        }

        if (!value) IsReidentifying = false;

        if (_tagQueue is not null)
        {
            _tagQueue.IsPaused = !value;
            if (value) _tagQueue.Signal();
        }

        _ = RefreshTagModelStatusAsync();
    }

    partial void OnAutoIndexingEnabledChanged(bool value)
    {
        if (_settings is not null)
        {
            _settings.Settings.AutoIndexingEnabled = value;
            _settings.Save();
        }

        if (_embeddingQueue is not null)
        {
            _embeddingQueue.IsPaused = !value;
            if (value) _embeddingQueue.Signal();
        }

        _ = RefreshSemanticStatusAsync();
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void SelectAppearance(string? mode) => AppearanceMode = AppearanceModes.Normalize(mode);

    [RelayCommand]
    private void SelectSettingsSection(string? name)
    {
        SettingsSection = name switch
        {
            "Browsing" => SettingsPageSection.Browsing,
            "Ai" => SettingsPageSection.Ai,
            "Data" => SettingsPageSection.Data,
            _ => SettingsPageSection.Appearance
        };
    }

    partial void OnSettingsSectionChanged(SettingsPageSection value)
    {
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsBrowsingSection));
        OnPropertyChanged(nameof(IsAiSection));
        OnPropertyChanged(nameof(IsDataSection));
    }

    [RelayCommand]
    private void ApplySortMode(string? mode) => SetSortMode(mode ?? "default");

    public void NotifyAppearanceChanged() => OnPropertyChanged(nameof(IsLightAppearance));

    [RelayCommand]
    private void ToggleAppearance()
        => AppearanceMode = ThemeColors.IsLight ? AppearanceModes.Dark : AppearanceModes.Light;

    /// <summary>按模型目录生成设置页的模型条目，并选出默认识别模型。</summary>
    private void BuildTagModelOptions(string? configuredModelId)
    {
        TagModels.Clear();
        _followDefaultModel = new TagModelOption("", "跟随默认");

        if (_tagCatalog is not null)
        {
            foreach (var entry in _tagCatalog.Models)
            {
                TagModels.Add(new TagModelOption(
                    entry.Definition.Id, entry.Definition.DisplayName));
            }
        }

        DefaultTagModelId = _tagCatalog?.Find(configuredModelId)?.Definition.Id
            ?? _tagCatalog?.Default.Definition.Id
            ?? "";
        RefreshTagModelRows();
    }

    /// <summary>刷新每个模型的安装状态与默认标记。</summary>
    private void RefreshTagModelRows()
    {
        foreach (var option in TagModels)
        {
            var entry = _tagCatalog?.Find(option.Id);
            option.IsReady = entry?.Manager.IsReady ?? false;
            if (!option.IsDownloading)
            {
                option.Status = option.IsReady ? "已就绪" : "未安装";
            }

            option.IsDefault = string.Equals(option.Id, DefaultTagModelId, StringComparison.Ordinal);
        }

        OnPropertyChanged(nameof(HasReadyTagModel));
        OnPropertyChanged(nameof(CanReidentifyTags));
    }

    /// <summary>按文件夹重建模型选择项；文件夹未指定模型时跟随默认。</summary>
    private void RebuildFolderTagModels()
    {
        FolderTagModels.Clear();
        _followDefaultModel ??= new TagModelOption("", "跟随默认");

        var choices = new List<TagModelOption> { _followDefaultModel };
        choices.AddRange(TagModels);

        foreach (var folder in Folders)
        {
            FolderTagModels.Add(new FolderTagModelOption(folder, choices));
        }
    }

    /// <summary>刷新标注状态文字。默认模型缺失、未下载与运行中分别给出对应文案。</summary>
    public async Task RefreshTagModelStatusAsync()
    {
        // 会话创建后才能确定实际设备，未推理时显示当前能力。
        AccelerationStatus = $"推理设备：{OrtAccelerator.Describe()}";
        RefreshTagModelRows();

        // 下载过程中保留文件与进度文案，等其他入口的刷新结束后再恢复覆盖率状态。
        if (IsTagModelDownloading) return;

        if (_tagCatalog is null)
        {
            IsTagging = false;
            TagModelStatus = "识别不可用";
            return;
        }

        var entry = _tagCatalog.Resolve(DefaultTagModelId);
        IsTagModelReady = entry.Manager.IsReady;
        if (!IsTagModelReady)
        {
            IsTagging = false;
            TagModelStatus = $"{entry.Definition.DisplayName} 未安装";
            return;
        }

        if (_tagQueue is null) return;

        var stats = await _tagQueue.GetStatsAsync();
        ApplyTagProgress(stats.Done, stats.Pending);
        if (!AutoTaggingEnabled && !IsReidentifying)
        {
            IsTagging = false;
            TagModelStatus = "识别已暂停";
            return;
        }

        TagModelStatus = FormatTaggingStatus(stats.Done, stats.Pending);
    }

    private void ApplyTagProgress(int done, int pending)
    {
        var total = done + pending;
        TagProgress = total > 0 ? (double)done / total : 0;
        IsTagging = pending > 0;
    }

    /// <summary>识别状态文案，待处理时带上已识别数量。</summary>
    private static string FormatTaggingStatus(int done, int pending) =>
        pending > 0 ? $"正在识别… 已识别 {done} / {done + pending}" : "识别就绪";

    private void OnTaggingProgressChanged(object? sender, TaggingProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var wasReidentifying = IsReidentifying;

            // 首张推理结束后会话已创建，此时显示的才是实际设备。
            AccelerationStatus = $"推理设备：{OrtAccelerator.Describe()}";

            // 下载期间保留文件与进度文案，队列的覆盖状态不能覆盖它。
            if (!IsTagModelDownloading && (AutoTaggingEnabled || wasReidentifying))
            {
                ApplyTagProgress(progress.Done, progress.Pending);
                TagModelStatus = FormatTaggingStatus(progress.Done, progress.Pending);
            }

            if (wasReidentifying && progress.Pending == 0)
            {
                IsReidentifying = false;
                _ = FinishTagRefreshAsync();
            }
            else if (_lastTagPending > 0 && progress.Pending == 0)
            {
                _ = FinishTagRefreshAsync();
            }

            // 当前选中图识别完成时刷新一次，详情栏无需手动切换。
            if (SelectedItem is { } item
                && string.Equals(progress.CurrentFile, item.FileName, StringComparison.Ordinal))
            {
                _ = LoadSelectedTagsAsync();
            }

            _lastTagPending = progress.Pending;
        });
    }

    /// <summary>清除现有 AI 标签并按各文件夹的模型重新识别全部图片；手动结果保留。</summary>
    public async Task ReidentifyTagsAsync()
    {
        if (_tagCatalog is null || !HasReadyTagModel || _tagQueue is null || IsReidentifying)
        {
            if (!HasReadyTagModel) TagModelStatus = "请先下载识别模型";
            return;
        }

        IsReidentifying = true;
        TagModelStatus = "正在重置识别结果…";
        StatusText = "正在重新识别标签…";
        _completionStatusText = "重新识别完成";
        try
        {
            await _tagQueue.ReidentifyAllAsync();
            await LoadTagsAsync();
            await LoadSelectedTagsAsync();
            await RefreshTagModelStatusAsync();
        }
        catch (Exception ex)
        {
            IsReidentifying = false;
            _completionStatusText = null;
            TagModelStatus = $"重新识别失败：{ex.Message}";
        }
    }

    /// <summary>重新分析单张图片的标签；使用所属文件夹的模型。</summary>
    public async Task ReanalyzeAssetAsync(AssetListItem item)
    {
        if (_tagQueue is null || _tagCatalog is null) return;

        var modelId = await _tagQueue.GetModelIdForAssetAsync(item.Id);
        var entry = _tagCatalog.Resolve(modelId);
        if (!entry.Manager.IsReady)
        {
            StatusText = $"{entry.Definition.DisplayName} 未安装，无法重新分析";
            return;
        }

        await _tagQueue.ReidentifyAssetAsync(item.Id);
        _completionStatusText = "重新分析完成";
        StatusText = $"正在重新分析 {item.FileName}";
    }

    /// <summary>重新分析指定文件夹的标签；使用该文件夹的模型。</summary>
    public async Task ReanalyzeFolderAsync(Folder folder)
    {
        if (_tagQueue is null || _tagCatalog is null) return;

        var entry = _tagCatalog.Resolve(folder.TagModelId);
        if (!entry.Manager.IsReady)
        {
            StatusText = $"{entry.Definition.DisplayName} 未安装，无法重新分析";
            return;
        }

        await _tagQueue.ReidentifyFolderAsync(folder.Id);
        _completionStatusText = "重新分析完成";
        StatusText = $"正在按 {entry.Definition.DisplayName} 重新分析 {folder.Name}";
    }

    /// <summary>标注队列排空后的收尾，清理空标签并刷新侧栏与详情栏。</summary>
    private async Task FinishTagRefreshAsync()
    {
        try
        {
            await PruneUnusedTagsAsync();
            await LoadTagsAsync();
            await LoadSelectedTagsAsync();
            await RefreshTagModelStatusAsync();

            if (_completionStatusText is { } text)
            {
                _completionStatusText = null;
                StatusText = text;
            }
        }
        catch (Exception ex)
        {
            _completionStatusText = null;
            TagModelStatus = $"识别完成，但刷新标签失败：{ex.Message}";
        }
    }

    /// <summary>下载默认标注模型。未安装时详情栏给出提示。</summary>
    [RelayCommand]
    private Task DownloadTagModelAsync() => DownloadModelAsync(DefaultTagModelId);

    /// <summary>下载指定模型；设置页按行触发。</summary>
    public async Task DownloadModelAsync(string? modelId)
    {
        var entry = _tagCatalog?.Find(modelId);
        if (entry is null)
        {
            return;
        }

        var option = TagModels.FirstOrDefault(option =>
            string.Equals(option.Id, entry.Definition.Id, StringComparison.Ordinal));
        if (option is { IsDownloading: true })
        {
            return;
        }

        var isDefault = string.Equals(
            entry.Definition.Id, DefaultTagModelId, StringComparison.Ordinal);
        if (option is not null)
        {
            option.IsDownloading = true;
            option.Progress = 0;
            option.Status = "准备下载…";
        }

        if (isDefault)
        {
            IsTagModelDownloading = true;
            TagModelStatus = "准备下载识别模型…";
        }

        using var cts = new CancellationTokenSource();
        _tagModelDownloads[entry.Definition.Id] = cts;

        var failed = false;
        try
        {
            var progress = new Progress<ModelDownloadProgress>(report =>
            {
                if (cts.IsCancellationRequested) return;

                var percent = report.Total > 0
                    ? (int)(report.Downloaded * 100 / report.Total)
                    : 0;
                var fileLabel = report.FileCount > 1
                    ? $"{report.FileIndex}/{report.FileCount} · {report.FileName}"
                    : report.FileName;

                if (option is not null)
                {
                    option.Progress = percent / 100.0;
                    option.Status = $"下载 {fileLabel} {percent}%";
                }

                if (isDefault) TagModelStatus = $"下载识别模型 {fileLabel} {percent}%";
            });
            await entry.Manager.DownloadAsync(progress, cts.Token);
            _tagQueue?.Signal();
        }
        catch (OperationCanceledException)
        {
            failed = true;
            if (option is not null) option.Status = "下载已取消";
            if (isDefault) TagModelStatus = "下载已取消";
        }
        catch (Exception ex)
        {
            failed = true;
            if (option is not null) option.Status = ex.Message;
            if (isDefault) TagModelStatus = ex.Message;
        }
        finally
        {
            _tagModelDownloads.Remove(entry.Definition.Id);
            if (option is not null) option.IsDownloading = false;
            if (isDefault) IsTagModelDownloading = false;
            if (!failed) await RefreshTagModelStatusAsync();
        }
    }

    /// <summary>取消指定模型的下载；临时文件会一并删除。</summary>
    public void CancelTagModelDownload(string modelId)
    {
        if (!_tagModelDownloads.TryGetValue(modelId, out var cts)) return;

        var option = TagModels.FirstOrDefault(option =>
            string.Equals(option.Id, modelId, StringComparison.Ordinal));
        if (option is not null) option.Status = "正在取消…";
        cts.Cancel();
    }

    /// <summary>卸载指定模型；该模型负责的图片会暂停识别，直到重新下载。</summary>
    public async Task UninstallTagModelAsync(string modelId)
    {
        var entry = _tagCatalog?.Find(modelId);
        if (entry is null) return;

        CancelTagModelDownload(modelId);
        try
        {
            await entry.Tagger.ReleaseIfIdleAsync(TimeSpan.Zero);
            entry.Manager.Remove();
        }
        catch (Exception ex)
        {
            StatusText = $"卸载失败：{ex.Message}";
            return;
        }

        await RefreshTagModelStatusAsync();
        _tagQueue?.Signal();
        StatusText = $"{entry.Definition.DisplayName} 已卸载";
    }

    /// <summary>切换默认模型；各文件夹仍可单独指定。</summary>
    public async Task SetDefaultTagModelAsync(string modelId)
    {
        if (_tagCatalog?.Find(modelId) is null)
        {
            return;
        }

        DefaultTagModelId = modelId;
        if (_settings is not null)
        {
            _settings.Settings.TagModelId = modelId;
            _settings.Save();
        }

        await RefreshTagModelStatusAsync();
        _tagQueue?.Signal();
        StatusText = $"默认识别模型已切换为 {_tagCatalog.Resolve(modelId).Definition.DisplayName}";
    }

    /// <summary>设置文件夹使用的模型；切换到已安装模型时清空该文件夹的旧结果并重跑。</summary>
    public async Task SetFolderModelAsync(Folder folder, string modelId)
    {
        if (_folders is null) return;

        modelId ??= "";
        if (string.Equals(folder.TagModelId, modelId, StringComparison.Ordinal)) return;

        await _folders.SetTagModelAsync(folder.Id, modelId);
        folder.TagModelId = modelId;

        var resolved = _tagCatalog?.Resolve(string.IsNullOrEmpty(modelId)
            ? DefaultTagModelId
            : modelId);
        if (resolved is not null && resolved.Manager.IsReady && _tagQueue is not null)
        {
            await _tagQueue.ReidentifyFolderAsync(folder.Id);
            StatusText = $"{folder.Name} 将使用 {resolved.Definition.DisplayName} 重新识别";
        }
        else
        {
            StatusText = $"{folder.Name} 已切换模型，下载后开始识别";
        }

        // 设置页的下拉框与右键菜单共用同一份文件夹对象，就地同步选择即可，
        // 重新加载文件夹列表会让正在处理的选择事件重入。
        var option = FolderTagModels.FirstOrDefault(item => item.Folder.Id == folder.Id);
        if (option is not null)
        {
            option.Selected = option.Choices.FirstOrDefault(choice =>
                                 choice.Id == modelId) ?? option.Choices[0];
        }
    }

    public IReadOnlyList<AssetListItem> SelectedItems => _selectedItems;

    public bool IsLightWall => CurrentView == ViewMode.LightWall;
    public bool IsGalaxy => CurrentView == ViewMode.Galaxy;
    public bool IsGrid => CurrentView == ViewMode.Grid;
    public bool IsAllSelected => ActiveCollection == LibraryCollection.All;
    public bool IsUntaggedSelected => ActiveCollection == LibraryCollection.Untagged;
    public bool IsSearchActive => ActiveCollection == LibraryCollection.Search;

    /// <summary>当前列表是否按语义相关度排序；为真时图库使用等高方格，让名次可读。</summary>
    public bool IsSemanticResult => IsSearchActive && _lastSearchWasSemantic;
    public bool IsMultiSelection => SelectionCount > 1;
    public bool HasSelection => SelectedItem is not null;

    /// <summary>导入进行中或离开图库时隐藏批量操作条，避免与视图切换药丸占用同一条基线。</summary>
    public bool ShowBatchBar => IsGrid && IsMultiSelection && !IsImporting;
    public bool HasFolders => Folders.Count > 0;
    public bool HasTags => Tags.Count > 0;
    public bool HasSelectedCharacters => SelectedCharacters.Count > 0;
    public bool HasSelectedSeries => SelectedSeriesText.Length > 0;

    partial void OnSelectedSeriesTextChanged(string value) => OnPropertyChanged(nameof(HasSelectedSeries));

    public bool IsEmpty => Items.Count == 0;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>搜索框提示随语义开关变化，避免关闭语义后仍提示语义检索。</summary>
    public string SearchHint => SemanticEnabled
        ? "搜索文件名、标签或画面内容"
        : "搜索文件名或标签";

    public string SelectedDimensions => SelectedItem is { Width: > 0, Height: > 0 } item
        ? $"{item.Width} × {item.Height}"
        : "尺寸未知";

    public string SelectedDateText => SelectedItem?.TakenAt?.ToString("yyyy-MM-dd HH:mm") ?? "时间未知";

    public string SelectedMeta => $"{SelectedDimensions} · {SelectedDateText}";

    public string SelectedLocation => string.IsNullOrWhiteSpace(SelectedItem?.PrimaryPath)
        ? "—"
        : SelectedItem.PrimaryPath;

    public string CollectionTitle => ActiveCollection switch
    {
        LibraryCollection.Untagged => "未加标签",
        LibraryCollection.Folder => SelectedFolder?.Name ?? "文件夹",
        LibraryCollection.Search => "搜索结果",
        _ => "全部图片"
    };

    public string CollectionKind => ActiveCollection switch
    {
        LibraryCollection.Untagged => "智能筛选",
        LibraryCollection.Folder => "文件夹",
        LibraryCollection.Search => "检索",
        _ => "图库"
    };

    public string SelectionSummary => $"已选择 {SelectionCount} 张";

    public string SortLabel => SortMode switch
    {
        "taken" => "拍摄时间",
        "name" => "文件名",
        "rating" => "评分",
        _ => "默认排序"
    };

    public bool IsSortDefault => SortMode == "default";
    public bool IsSortTaken => SortMode == "taken";
    public bool IsSortName => SortMode == "name";
    public bool IsSortRating => SortMode == "rating";

    public string EmptyTitle => IsSearchActive
        ? "没有找到匹配的图片"
        : ActiveCollection == LibraryCollection.Untagged && HasFolders
            ? "所有图片都已加上标签"
            : HasFolders
                ? "这里还没有图片"
                : "把图片收进 ClipNest";

    public string EmptyDescription => IsSearchActive
        ? "试试更短的关键词，或使用 #标签、folder: 路径来筛选。"
        : ActiveCollection == LibraryCollection.Untagged && HasFolders
            ? "继续保持，或者切换到全部图片继续整理。"
            : HasFolders
                ? "给文件夹补充图片后点击刷新，新图片会自动建立索引。"
                : "导入一个文件夹，ClipNest 会生成缩略图并在本地完成索引。";

    public string ViewHint => CurrentView switch
    {
        ViewMode.LightWall => "悬停聚焦 · 双击打开",
        ViewMode.Galaxy => "拖拽平移 · 滚轮缩放 · 双击打开",
        _ => "双击打开 · Ctrl 多选 · 方向键浏览"
    };

    partial void OnCurrentViewChanged(ViewMode value)
    {
        OnPropertyChanged(nameof(IsLightWall));
        OnPropertyChanged(nameof(IsGalaxy));
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(ViewHint));
        OnPropertyChanged(nameof(ShowBatchBar));

        if (_settings is not null)
        {
            _settings.Settings.LastView = value.ToString();
            _settings.Save();
        }

        if (value == ViewMode.Galaxy)
        {
            _ = LoadGalaxyAsync();
        }
        else if (Items.Count > 0)
        {
            StatusText = $"{Items.Count} 张图片";
        }
    }

    partial void OnActiveCollectionChanged(LibraryCollection value)
    {
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsUntaggedSelected));
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(IsSemanticResult));
        OnPropertyChanged(nameof(CollectionTitle));
        OnPropertyChanged(nameof(CollectionKind));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    partial void OnSelectionCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsMultiSelection));
        OnPropertyChanged(nameof(ShowBatchBar));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    partial void OnSearchTextChanged(string value)
        => OnPropertyChanged(nameof(HasSearchText));

    partial void OnImportProcessedChanged(int value)
    {
        OnPropertyChanged(nameof(ImportText));
        OnPropertyChanged(nameof(ImportFraction));
    }

    partial void OnImportTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ImportText));
        OnPropertyChanged(nameof(ImportFraction));
        OnPropertyChanged(nameof(IsImportIndeterminate));
    }

    partial void OnIsImportingChanged(bool value)
        => OnPropertyChanged(nameof(ShowBatchBar));

    partial void OnIsInspectorOpenChanged(bool value)
    {
        if (_settings is null) return;
        _settings.Settings.InspectorOpen = value;
        _settings.Save();
    }

    partial void OnThumbnailSizeChanged(double value)
    {
        if (_settings is null) return;
        _settings.Settings.ThumbnailSize = value;
        SaveSettingsDeferred();
    }

    partial void OnSortModeChanged(string value)
    {
        OnPropertyChanged(nameof(SortLabel));
        OnPropertyChanged(nameof(IsSortDefault));
        OnPropertyChanged(nameof(IsSortTaken));
        OnPropertyChanged(nameof(IsSortName));
        OnPropertyChanged(nameof(IsSortRating));

        if (_settings is not null)
        {
            _settings.Settings.SortMode = value;
            _settings.Save();
        }

        _ = ApplySortInPlaceAsync();
    }

    private void SaveSettingsDeferred()
    {
        if (_settings is null) return;
        _settingsSaveTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _settingsSaveTimer?.Stop();
                _settings?.Save();
            });
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private static string NormalizeSortMode(string? mode) => mode switch
    {
        "taken" => "taken",
        "name" => "name",
        "rating" => "rating",
        _ => "default"
    };

    public void SetSortMode(string mode)
    {
        SortMode = NormalizeSortMode(mode);
    }

    private IEnumerable<AssetListItem> SortItems(IEnumerable<AssetListItem> items)
        => SortItems(items, SortMode);

    private static IEnumerable<AssetListItem> SortItems(
        IEnumerable<AssetListItem> items, string sortMode) => sortMode switch
    {
        "name" => items.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase),
        "taken" => items.OrderByDescending(item => item.TakenAt ?? item.CreatedAt),
        "rating" => items.OrderByDescending(item => item.Rating)
            .ThenByDescending(item => item.TakenAt ?? item.CreatedAt),
        _ => items
    };

    /// <summary>
    /// 在后台按新顺序重排现有列表。全库排序在 UI 线程上会造成明显停顿，
    /// 排序期间若列表被新查询替换或又切了一次排序，就放弃这次结果。
    /// </summary>
    internal async Task ApplySortInPlaceAsync()
    {
        if (_baseItems.Count == 0) return;

        var baseItems = _baseItems;
        var sortMode = SortMode;
        var token = _collectionToken;

        var sorted = sortMode == "default"
            ? (IReadOnlyList<AssetListItem>)baseItems
            : await Task.Run(() => SortItems(baseItems, sortMode).ToList());

        if (token != _collectionToken || sortMode != SortMode ||
            !ReferenceEquals(baseItems, _baseItems))
        {
            return;
        }

        if (Items.SequenceEqual(sorted)) return;

        Items.ReplaceAll(sorted);
        ClearSelectionIfMissing();
    }

    /// <summary>整批替换列表；返回 false 表示结果已被更新的查询取代。</summary>
    private async Task<bool> ReplaceItemsAsync(IEnumerable<AssetListItem> source, int token)
    {
        // 保留集合的自然顺序，即最近添加或检索相关度；切换回「默认排序」时才能还原。
        // 语义结果的相关度顺序优先于排序设置，否则名次会被重排打乱。
        _baseItems = source.ToList();
        var baseItems = _baseItems;
        var sortMode = IsSemanticResult ? "default" : SortMode;
        var items = sortMode == "default"
            ? (IReadOnlyList<AssetListItem>)baseItems
            : await Task.Run(() => SortItems(baseItems, sortMode).ToList());

        if (token != _collectionToken) return false;

        Items.ReplaceAll(items);
        ClearSelectionIfMissing();
        _ = EnsureEnvironmentAsync();
        return true;
    }

    /// <summary>
    /// 环境层优先使用当前选中的图片，没有选中时取列表首图，供窗口背景做模糊衬底。
    /// </summary>
    private async Task EnsureEnvironmentAsync()
    {
        if (EnvironmentBitmap is not null || _thumbnails is null) return;

        var item = SelectedItem ?? (Items.Count > 0 ? Items[0] : null);
        if (item is null) return;

        var bitmap = await _thumbnails.GetAsync(item, 512);
        if (bitmap is not null && EnvironmentBitmap is null) EnvironmentBitmap = CreateAmbient(bitmap);
    }

    /// <summary>
    /// 环境层用小尺寸位图代替实时高斯模糊。先缩到 128px 再缩到 32px，
    /// 放大铺满窗口后形成柔和色场，既保留图片颜色，又不必每帧重算模糊。
    /// </summary>
    private static Bitmap? CreateAmbient(Bitmap source)
    {
        if (source.PixelSize.Width <= 0) return null;

        var first = new RenderTargetBitmap(new PixelSize(128, 72), new Vector(96, 96));
        using (var context = first.CreateDrawingContext())
        {
            context.DrawImage(source, new Rect(0, 0, 128, 72));
        }

        var second = new RenderTargetBitmap(new PixelSize(32, 18), new Vector(96, 96));
        using (var context = second.CreateDrawingContext())
        {
            context.DrawImage(first, new Rect(0, 0, 32, 18));
        }

        return second;
    }

    /// <summary>由瀑布流控件回写多选结果。</summary>
    public void ApplySelection(IReadOnlyList<AssetListItem> items)
    {
        _selectedItems = items;
        OnPropertyChanged(nameof(SelectedItems));
        SelectionCount = items.Count;
        SelectedItem = items.Count > 0 ? items[^1] : null;
    }

    public void ClearSelection()
    {
        _selectedItems = Array.Empty<AssetListItem>();
        OnPropertyChanged(nameof(SelectedItems));
        SelectionCount = 0;
        SelectedItem = null;
    }

    partial void OnSemanticEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchHint));

        if (_settings is not null)
        {
            _settings.Settings.SemanticEnabled = value;
            _settings.Save();
        }

        if (!value) return;

        if (!IsModelReady)
        {
            SemanticStatus = "语义模型未安装，请先下载";
            return;
        }

        _embeddingQueue?.Signal();
        _tagQueue?.Signal();
        _ = RefreshSemanticStatusAsync();
    }

    /// <summary>刷新语义状态文字。模型缺失、未下载与索引中分别给出对应文案。</summary>
    public async Task RefreshSemanticStatusAsync()
    {
        if (_models is null)
        {
            SemanticStatus = "语义不可用";
            return;
        }

        IsModelReady = _models.IsReady;
        if (!IsModelReady)
        {
            SemanticStatus = "语义模型未安装";
            return;
        }

        if (_embeddingQueue is null) return;

        var stats = await _embeddingQueue.GetStatsAsync();
        if (!AutoIndexingEnabled)
        {
            SemanticStatus = $"语义已暂停 · {stats.Ready} 张";
            return;
        }

        var total = stats.Ready + stats.Pending + stats.Error;
        SemanticStatus = stats.Pending > 0
            ? $"语义索引 {stats.Ready}/{total}"
            : $"语义就绪 · {stats.Ready} 张";
    }

    private void OnEmbeddingProgressChanged(object? sender, EmbeddingProgress progress)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (AutoIndexingEnabled)
            {
                var total = progress.Ready + progress.Pending + progress.Error;
                SemanticStatus = progress.Pending > 0
                    ? $"语义索引 {progress.Ready}/{total}"
                    : $"语义就绪 · {progress.Ready} 张";
            }

            // 索引刚完成时若在星系视图，重新计算布局。
            if (_lastPending > 0 && progress.Pending == 0 && IsGalaxy)
                _ = LoadGalaxyAsync();

            _lastPending = progress.Pending;
        });
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (_models is null || IsModelDownloading) return;

        using var cts = new CancellationTokenSource();
        _semanticModelDownload = cts;
        IsModelDownloading = true;
        try
        {
            var progress = new Progress<ClipModelDownloadProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;

                ModelDownloadProgress = p.Total > 0 ? (double)p.Downloaded / p.Total : 0;
                var percent = (int)(ModelDownloadProgress * 100);
                SemanticStatus = $"下载 {Path.GetFileName(p.FileName)} {percent}%";
            });

            await _models.DownloadAsync(progress, cts.Token);

            IsModelReady = _models.IsReady;
            if (_embeddingQueue is not null)
            {
                await _embeddingQueue.ResetSkippedAsync();
                _embeddingQueue.Signal();
            }

            await RefreshSemanticStatusAsync();
        }
        catch (OperationCanceledException)
        {
            SemanticStatus = "下载已取消";
        }
        catch (Exception ex)
        {
            SemanticStatus = $"模型下载失败：{ex.Message}";
        }
        finally
        {
            _semanticModelDownload = null;
            IsModelDownloading = false;
            ModelDownloadProgress = 0;
        }
    }

    /// <summary>取消语义模型下载。</summary>
    public void CancelSemanticModelDownload()
    {
        if (_semanticModelDownload is null) return;
        SemanticStatus = "正在取消…";
        _semanticModelDownload.Cancel();
    }

    /// <summary>卸载语义模型；语义搜索与索引会暂停，直到重新下载。</summary>
    public async Task UninstallSemanticModelAsync()
    {
        if (_models is null) return;

        CancelSemanticModelDownload();
        try
        {
            if (_clipEncoder is not null) await _clipEncoder.ReleaseAsync();
            _models.Remove();
        }
        catch (Exception ex)
        {
            StatusText = $"卸载失败：{ex.Message}";
            return;
        }

        IsModelReady = false;
        await RefreshSemanticStatusAsync();
        StatusText = "语义模型已卸载";
    }

    [RelayCommand] private void ShowWall() => CurrentView = ViewMode.LightWall;
    [RelayCommand] private void ShowGalaxy() => CurrentView = ViewMode.Galaxy;
    [RelayCommand] private void ShowGrid() => CurrentView = ViewMode.Grid;

    public async Task LoadAsync()
    {
        if (_search is null) return;
        var token = InvalidateCollectionQueries();
        var items = await _search.GetRecentAsync(NoLimit);
        if (token != _collectionToken) return;
        if (!await ReplaceItemsAsync(items, token)) return;
        TotalCount = items.Count;
        HighlightedIds = null;
        StatusText = $"{items.Count} 张图片";
    }

    /// <summary>读取星系快照并计算布局，填充簇与节点集合。</summary>
    public async Task LoadGalaxyAsync()
    {
        if (_galaxy is null) return;

        GalaxyLayoutResult result;
        try
        {
            var snapshot = await _galaxy.GetSnapshotAsync();
            result = await Task.Run(() => GalaxyLayout.Build(snapshot));
        }
        catch (Exception ex)
        {
            StatusText = $"星系布局失败：{ex.Message}";
            return;
        }

        GalaxyClusters.Clear();
        foreach (var cluster in result.Clusters) GalaxyClusters.Add(cluster);
        GalaxyNodes.Clear();
        foreach (var node in result.Nodes) GalaxyNodes.Add(node);

        if (IsGalaxy)
            StatusText = $"{GalaxyClusters.Count} 个簇 · {GalaxyNodes.Count} 张";
    }

    /// <summary>点击簇时，未分类簇显示全部，其余按标签路径搜索。</summary>
    public async Task SearchClusterAsync(GalaxyCluster cluster)
    {
        if (cluster.TagId is null)
        {
            await ShowAllAsync();
            return;
        }

        SearchText = FormatTagQuery(cluster.TagPath);
        await SearchAsync();
    }

    [RelayCommand]
    public async Task ShowAllAsync()
    {
        InvalidateCollectionQueries();
        SetSelectedFolderSilently(null);
        ActiveCollection = LibraryCollection.All;
        SearchText = "";
        SearchQueryLabel = "";
        await LoadAsync();
    }

    [RelayCommand]
    public async Task ShowUntaggedAsync()
    {
        if (_search is null) return;
        var token = InvalidateCollectionQueries();
        SetSelectedFolderSilently(null);
        SearchText = "";
        SearchQueryLabel = "";
        ActiveCollection = LibraryCollection.Untagged;
        try
        {
            var items = await _search.SearchAsync("is:untagged", NoLimit);
            if (token != _collectionToken) return;
            if (!await ReplaceItemsAsync(items, token)) return;
            StatusText = $"{items.Count} 张未加标签";
        }
        catch (Exception ex)
        {
            if (token != _collectionToken) return;
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (_search is null) return;
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await ShowAllAsync();
            return;
        }

        var token = InvalidateCollectionQueries();
        SetSelectedFolderSilently(null);
        SearchQueryLabel = SearchText;
        ActiveCollection = LibraryCollection.Search;

        try
        {
            var query = SearchText;
            // 语义开关开启且查询是纯文本时自动转为语义查询；命中现有标签名时优先按标签过滤。
            // SearchText 本身保持不变；模型缺失时仍走语义分支，由检索层给出明确提示。
            var semantic = SemanticEnabled && IsPlainSemanticQuery(query);
            var exactTag = false;
            if (semantic && _tags is not null)
            {
                exactTag = (await _tags.GetTagIdsByPathAsync(
                    query, includeDescendants: true)).Count > 0;
            }

            _lastSearchWasSemantic = semantic && !exactTag;
            OnPropertyChanged(nameof(IsSemanticResult));
            if (_lastSearchWasSemantic)
                query = $"sim:\"{query.Replace("\"", "\\\"")}\"";
            else if (exactTag)
                query = FormatTagQuery(query);

            var items = await _search.SearchAsync(query, NoLimit);
            if (token != _collectionToken) return;
            if (!await ReplaceItemsAsync(items, token)) return;
            HighlightedIds = Items.Select(item => item.Id).ToHashSet();
            StatusText = $"{Items.Count} 个结果";
        }
        catch (Exception ex)
        {
            if (token != _collectionToken) return;
            // 出错时结果不再标记为按相关度排序。
            _lastSearchWasSemantic = false;
            OnPropertyChanged(nameof(IsSemanticResult));
            StatusText = ex.Message;
        }
    }

    /// <summary>判断查询是否为可直接转为语义查询的纯文本。</summary>
    private static bool IsPlainSemanticQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith('-')) return false;
        if (text.StartsWith("sim:", StringComparison.OrdinalIgnoreCase)) return false;
        return text.IndexOfAny(new[] { '#', ':', '"', '|', '(', ')', '-' }) < 0;
    }

    /// <summary>点击左侧标签时按标签路径搜索。</summary>
    public async Task FilterByTagAsync(string tagPath)
    {
        if (_search is null || string.IsNullOrWhiteSpace(tagPath)) return;
        SetSelectedFolderSilently(null);
        SearchText = FormatTagQuery(tagPath);
        await SearchAsync();
    }

    /// <summary>
    /// 标签名可能含空格，AI 标签会把下划线替换为空格；整段路径需要引号才不会被拆词。
    /// </summary>
    private static string FormatTagQuery(string tagPath)
        => tagPath.Any(char.IsWhiteSpace) ? $"#\"{tagPath}\"" : $"#{tagPath}";

    public async Task LoadFoldersAsync()
    {
        if (_folders is null) return;
        var selectedId = SelectedFolder?.Id;
        var folders = await _folders.GetAllAsync();

        _suppressFolderFilter = true;
        try
        {
            Folders.Clear();
            foreach (var folder in folders) Folders.Add(folder);
            SelectedFolder = selectedId is null
                ? null
                : Folders.FirstOrDefault(folder => folder.Id == selectedId);
        }
        finally
        {
            _suppressFolderFilter = false;
        }

        RebuildFolderTagModels();
    }

    partial void OnSelectedFolderChanged(Folder? value)
    {
        OnPropertyChanged(nameof(IsAllSelected));
        // 标题由 SelectedFolder 计算得出。从 A 文件夹切到 B 时 ActiveCollection
        // 一直是 Folder，不会触发集合变更通知，必须在这里单独通知一次。
        OnPropertyChanged(nameof(CollectionTitle));
        if (_suppressFolderFilter || value is null) return;
        ActiveCollection = LibraryCollection.Folder;
        _ = FilterByFolderAsync(value);
    }

    /// <summary>作废仍在执行的集合查询，避免先发出的请求后返回时覆盖新选择。</summary>
    private int InvalidateCollectionQueries() => ++_collectionToken;

    private void SetSelectedFolderSilently(Folder? folder)
    {
        _suppressFolderFilter = true;
        try
        {
            SelectedFolder = folder;
        }
        finally
        {
            _suppressFolderFilter = false;
        }
    }

    private async Task FilterByFolderAsync(Folder folder)
    {
        if (_search is null) return;

        var token = InvalidateCollectionQueries();

        // 切换到文件夹等于更换集合，搜索框必须一起清空；
        // 否则输入框保留旧查询而结果是文件夹，状态自相矛盾。
        ActiveCollection = LibraryCollection.Folder;
        SearchText = "";
        SearchQueryLabel = "";

        try
        {
            var items = await _search.SearchAsync($"folder:\"{folder.Path}\"", NoLimit);
            if (token != _collectionToken) return;
            if (!await ReplaceItemsAsync(items, token)) return;
            HighlightedIds = Items.Select(item => item.Id).ToHashSet();
            StatusText = $"{folder.Name}：{Items.Count} 张";
        }
        catch (Exception ex)
        {
            if (token != _collectionToken) return;
            StatusText = ex.Message;
        }
    }

    public async Task AddFolderAsync(string path)
    {
        if (_scanner is null) return;
        IsImporting = true;
        ImportProcessed = 0;
        ImportTotal = 0;
        StatusText = "扫描中…";
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ImportProcessed = p.Processed;
                ImportTotal = p.Total;
            });
            var added = await _scanner.ScanAsync(path, progress);
            await LoadFoldersAsync();

            // 导入新文件夹不应把当前正在看的文件夹换成全部图片。
            var selected = SelectedFolder;
            if (selected is not null)
                await FilterByFolderAsync(selected);
            else
                await LoadAsync();

            if (CurrentView == ViewMode.Galaxy) await LoadGalaxyAsync();
            StatusText = $"已添加文件夹，{added} 个新文件";
            _embeddingQueue?.Signal();
            _tagQueue?.Signal();
            await RefreshSemanticStatusAsync();
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        if (_scanner is null) return;
        var folders = Folders.ToList();
        for (var i = 0; i < folders.Count; i++)
        {
            StatusText = $"刷新中 {i + 1}/{folders.Count}：{folders[i].Name}";
            await _scanner.ScanAsync(folders[i].Path);
        }

        await LoadFoldersAsync();
        var selected = SelectedFolder;
        if (selected is not null)
            await FilterByFolderAsync(selected);
        else
            await LoadAsync();
        if (CurrentView == ViewMode.Galaxy) await LoadGalaxyAsync();
        StatusText = $"已刷新 {folders.Count} 个文件夹";
        _embeddingQueue?.Signal();
        _tagQueue?.Signal();
        await RefreshSemanticStatusAsync();
    }

    public async Task RefreshFolderAsync(Folder folder)
    {
        if (_scanner is null) return;
        StatusText = $"正在刷新 {folder.Name}…";
        await _scanner.ScanAsync(folder.Path);
        await LoadFoldersAsync();
        if (CurrentView == ViewMode.Galaxy) await LoadGalaxyAsync();

        var selected = SelectedFolder;
        if (selected is not null && selected.Id == folder.Id)
        {
            await FilterByFolderAsync(selected);
            StatusText = $"已刷新 {selected.Name}：{Items.Count} 张";
        }
        else
        {
            await LoadAsync();
            StatusText = $"已刷新 {folder.Name}";
        }

        _embeddingQueue?.Signal();
        _tagQueue?.Signal();
        await RefreshSemanticStatusAsync();
    }

    public async Task RemoveFolderAsync(Folder folder)
    {
        if (_folders is null) return;

        await _folders.RemoveAsync(folder.Id);
        if (SelectedFolder?.Id == folder.Id)
            SetSelectedFolderSilently(null);

        // 图片随文件夹一起删除后，它们的标签会变成没有归属的空条目，必须同步清掉。
        if (_tags is not null)
        {
            await _tags.PruneUnusedAsync();
            await LoadTagsAsync();
        }

        await LoadFoldersAsync();
        await LoadAsync();
        ActiveCollection = LibraryCollection.All;
        if (CurrentView == ViewMode.Galaxy) await LoadGalaxyAsync();
        StatusText = $"已移除 {folder.Name}（未删除磁盘文件）";
        _embeddingQueue?.Signal();
        _tagQueue?.Signal();
        await RefreshSemanticStatusAsync();
    }

    /// <summary>把选中的图片复制或移动到目标文件夹，完成后刷新当前集合。</summary>
    public Task<BatchOutcome> TransferSelectionAsync(string targetFolder, FileTransferMode mode)
        => TransferAsync(SelectionSnapshot(), targetFolder, mode);

    /// <summary>复制或移动指定图片。右键菜单传入的是按下时锁定的集合。</summary>
    public async Task<BatchOutcome> TransferAsync(
        IReadOnlyList<AssetListItem> items, string targetFolder, FileTransferMode mode)
    {
        if (_operations is null || items.Count == 0) return BatchOutcome.None;

        StatusText = mode == FileTransferMode.Move
            ? $"正在移动 {items.Count} 张…"
            : $"正在复制 {items.Count} 张…";

        BatchOutcome outcome;
        try
        {
            outcome = await _operations.TransferAsync(items, targetFolder, mode);

            // 移动会让图片离开当前文件夹，刷新后才能反映真实集合。
            await RefreshLibraryAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"操作失败：{ex.Message}";
            return BatchOutcome.None;
        }

        var action = mode == FileTransferMode.Move ? "移动" : "复制";
        var folderName = FolderNameOf(targetFolder);
        var failure = outcome.HasErrors ? $"，{outcome.Errors.Count} 张失败" : "";
        StatusText = outcome.Succeeded > 0
            ? $"已{action} {outcome.Succeeded} 张到「{folderName}」{failure}"
            : outcome.HasErrors
                ? outcome.Errors[0]
                : $"选中的图片已经在「{folderName}」中";
        return outcome;
    }

    /// <summary>删除给定图片的全部文件。删除前必须由调用方向用户确认。</summary>
    public async Task<BatchOutcome> DeleteAssetsAsync(IReadOnlyList<AssetListItem> items)
    {
        if (_operations is null || items.Count == 0) return BatchOutcome.None;

        StatusText = $"正在删除 {items.Count} 张…";

        BatchOutcome outcome;
        try
        {
            outcome = await _operations.DeleteAsync(items);

            // 资产消失后，只被这些图片用过的标签会成为空条目，必须一并清理。
            if (_tags is not null) await _tags.PruneUnusedAsync();
            await RefreshLibraryAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
            return BatchOutcome.None;
        }

        var failure = outcome.HasErrors ? $"，{outcome.Errors.Count} 张未删除" : "";
        StatusText = outcome.Succeeded > 0
            ? $"已删除 {outcome.Succeeded} 张，可在回收站还原{failure}"
            : outcome.Errors.FirstOrDefault() ?? "没有可删除的图片";
        return outcome;
    }

    /// <summary>当前多选；没有多选时退化为详情栏里的那一张。</summary>
    public IReadOnlyList<AssetListItem> SelectionSnapshot()
    {
        if (SelectedItems.Count > 0) return SelectedItems;
        return SelectedItem is { } single
            ? new[] { single }
            : Array.Empty<AssetListItem>();
    }

    /// <summary>重新读取当前集合与侧栏。搜索、文件夹与全部图片各自按原条件重查。</summary>
    public async Task RefreshLibraryAsync()
    {
        await LoadFoldersAsync();

        if (ActiveCollection == LibraryCollection.Folder && SelectedFolder is { } folder)
            await FilterByFolderAsync(folder);
        else if (ActiveCollection == LibraryCollection.Search)
            await SearchAsync();
        else if (ActiveCollection == LibraryCollection.Untagged)
            await ShowUntaggedAsync();
        else
            await LoadAsync();

        await LoadTagsAsync();
        if (IsGalaxy) await LoadGalaxyAsync();
    }

    private static string FolderNameOf(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private void ClearSelectionIfMissing()
    {
        if (SelectedItem is null && _selectedItems.Count == 0) return;

        // 全选后列表可能达到数万条，逐条 Any 会退化为 O(n²)；先建一次 id 集合。
        var ids = new HashSet<long>(Items.Count);
        foreach (var item in Items) ids.Add(item.Id);

        if (SelectedItem is not null && !ids.Contains(SelectedItem.Id))
            SelectedItem = null;

        if (_selectedItems.Count == 0) return;
        var valid = _selectedItems.Where(item => ids.Contains(item.Id)).ToList();
        if (valid.Count == _selectedItems.Count) return;

        _selectedItems = valid;
        OnPropertyChanged(nameof(SelectedItems));
        SelectionCount = valid.Count;
        if (valid.Count == 0) SelectedItem = null;
    }

    partial void OnSelectedItemChanged(AssetListItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedDimensions));
        OnPropertyChanged(nameof(SelectedDateText));
        OnPropertyChanged(nameof(SelectedMeta));
        OnPropertyChanged(nameof(SelectedLocation));

        // 换图时放弃未提交的角色与作品编辑，避免把上一张的输入带到下一张。
        IsEditingCharacters = false;
        IsEditingSeries = false;
        CharacterDraft = "";
        SeriesDraft = "";

        _ = LoadSelectedTagsAsync();
        _ = LoadPreviewAsync(value);
    }

    /// <summary>窗口关闭前把防抖中的界面设置立即落盘。</summary>
    public void SaveSettingsNow()
    {
        _settingsSaveTimer?.Stop();
        _settings?.Save();
    }

    /// <summary>补齐旧数据里缺失的图片尺寸，启动时执行一次。</summary>
    public async Task BackfillDimensionsAsync()
    {
        if (_scanner is not null) await _scanner.BackfillDimensionsAsync();
    }

    /// <summary>
    /// 清掉没有任何文件指向的旧资产。文件被原地替换后旧内容会失去全部路径，
    /// 对应的语义向量与识别结果随外键一起删除，索引和图片数量保持一致。
    /// </summary>
    public async Task PruneOrphansAsync()
    {
        if (_scanner is not null) await _scanner.PruneOrphansAsync();
    }

    /// <summary>清理没有图片引用的标签，供启动时清理移除文件夹后留下的空条目。</summary>
    public async Task PruneUnusedTagsAsync()
    {
        if (_tags is not null) await _tags.PruneUnusedAsync();
    }

    public async Task LoadTagsAsync()
    {
        if (_tags is null) return;
        var tree = await _tags.GetTreeAsync();
        Tags.Clear();

        void Walk(IEnumerable<Tag> nodes, string prefix, int depth)
        {
            foreach (var tag in nodes)
            {
                var path = string.IsNullOrEmpty(prefix) ? tag.Name : $"{prefix}/{tag.Name}";
                Tags.Add(new TagTreeItemViewModel(tag.Id, tag.Name, path, depth));
                Walk(tag.Children, path, depth + 1);
            }
        }

        Walk(tree, "", 0);
    }

    [RelayCommand]
    public async Task AddTagAsync()
    {
        if (_tags is null || string.IsNullOrWhiteSpace(NewTag)) return;

        var targets = SelectedItems.Count > 0
            ? SelectedItems
            : SelectedItem is null
                ? Array.Empty<AssetListItem>()
                : new[] { SelectedItem };

        if (targets.Count == 0) return;

        var tagId = await _tags.GetOrCreateAsync(NewTag);
        foreach (var item in targets)
            await _tags.AssignAsync(item.Id, tagId);

        NewTag = "";
        await LoadSelectedTagsAsync();
        await LoadTagsAsync();
        StatusText = targets.Count > 1
            ? $"已为 {targets.Count} 张图片添加标签"
            : "已添加标签";
    }

    public async Task RemoveTagAsync(Tag tag)
    {
        if (SelectedItem is null || _tags is null) return;
        await _tags.RemoveAsync(SelectedItem.Id, tag.Id);
        await _tags.PruneUnusedAsync();
        await LoadSelectedTagsAsync();
        await LoadTagsAsync();
    }

    private async Task LoadSelectedTagsAsync()
    {
        var item = SelectedItem;
        if (item is null)
        {
            SelectedTags.Clear();
            SelectedCharacters.Clear();
            SelectedSeriesText = "";
            return;
        }

        if (_tags is null)
        {
            SelectedTags.Clear();
            SelectedCharacters.Clear();
            SelectedSeriesText = "";
            return;
        }

        var tags = await _tags.GetTagsForAssetAsync(item.Id);
        if (SelectedItem?.Id != item.Id) return;
        SelectedTags.Clear();
        foreach (var tag in tags) SelectedTags.Add(tag);

        // 角色与作品按类别单独读取；手动编辑写的也是同一类别，展示逻辑不分来源。
        var characters = await _tags.GetTagsByCategoryAsync(item.Id, AiTagCategory.Character);
        var series = await _tags.GetTagsByCategoryAsync(item.Id, AiTagCategory.Series);
        if (SelectedItem?.Id != item.Id) return;

        SelectedCharacters.Clear();
        foreach (var tag in characters) SelectedCharacters.Add(tag);
        SelectedSeriesText = series.FirstOrDefault()?.Name ?? "";
    }

    [RelayCommand]
    private void BeginEditCharacters()
    {
        CharacterDraft = string.Join("，", SelectedCharacters.Select(tag => tag.Name));
        IsEditingCharacters = true;
    }

    [RelayCommand]
    private void CancelCharacters()
    {
        IsEditingCharacters = false;
        CharacterDraft = "";
    }

    /// <summary>用输入框里的名字替换当前选中图片的角色；清空即清除角色。</summary>
    [RelayCommand]
    private async Task CommitCharactersAsync()
    {
        if (_tags is null) return;

        var targets = SelectedAssetIds();
        if (targets.Count == 0) return;

        var names = SplitNames(CharacterDraft);
        IsEditingCharacters = false;
        try
        {
            await _tags.ReplaceCategoryTagsAsync(targets, AiTagCategory.Character, names);
            await LoadSelectedTagsAsync();
            await LoadTagsAsync();
            StatusText = names.Count == 0
                ? $"已清除 {targets.Count} 张图片的角色"
                : $"已更新 {targets.Count} 张图片的角色";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void BeginEditSeries()
    {
        SeriesDraft = SelectedSeriesText;
        IsEditingSeries = true;
    }

    [RelayCommand]
    private void CancelSeries()
    {
        IsEditingSeries = false;
        SeriesDraft = "";
    }

    /// <summary>用输入框里的名字替换当前选中图片的作品；清空即清除作品。</summary>
    [RelayCommand]
    private async Task CommitSeriesAsync()
    {
        if (_tags is null) return;

        var targets = SelectedAssetIds();
        if (targets.Count == 0) return;

        var name = SeriesDraft.Trim();
        IsEditingSeries = false;
        try
        {
            await _tags.ReplaceCategoryTagsAsync(
                targets,
                AiTagCategory.Series,
                name.Length == 0 ? Array.Empty<string>() : new[] { name });
            await LoadSelectedTagsAsync();
            await LoadTagsAsync();
            StatusText = name.Length == 0
                ? $"已清除 {targets.Count} 张图片的作品"
                : $"已更新 {targets.Count} 张图片的作品";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private List<long> SelectedAssetIds()
    {
        if (SelectedItems.Count > 0)
        {
            return SelectedItems.Select(item => item.Id).ToList();
        }

        return SelectedItem is { } single ? new List<long> { single.Id } : new List<long>();
    }

    /// <summary>角色名可能包含空格，只按常见分隔符拆分。</summary>
    private static IReadOnlyList<string> SplitNames(string draft)
        => draft.Split(
                new[] { ',', '，', ';', '；', '、', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task LoadPreviewAsync(AssetListItem? item)
    {
        if (item is null || _thumbnails is null)
        {
            PreviewBitmap = null;
            return;
        }

        // 预览框约 200×200 逻辑像素，按 2 倍屏取图；长图按自身比例换算长边，
        // 否则短边只有几十像素，铺满 200 像素宽的框时会明显发虚。
        var ratio = item is { Width: > 0, Height: > 0 }
            ? item.Width.Value / (double)item.Height.Value
            : (double?)null;
        var size = ThumbnailSizing.ForBox(220, 200, ratio, 2, 1600);

        var bitmap = await _thumbnails.GetAsync(item, size);
        if (SelectedItem?.Id != item.Id) return;

        PreviewBitmap = bitmap;
        if (bitmap is not null) EnvironmentBitmap = CreateAmbient(bitmap);
    }
}
