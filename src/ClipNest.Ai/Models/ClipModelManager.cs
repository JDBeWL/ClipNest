namespace ClipNest.Ai.Models;

/// <summary>
/// 语义模型下载进度。
/// </summary>
public sealed record ClipModelDownloadProgress(string FileName, long Downloaded, long Total);

/// <summary>
/// 语义模型文件管理，负责检查就绪状态并按需下载。
/// </summary>
public sealed class ClipModelManager
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(60)
    };

    public ClipModelManager(string root, ClipModelDefinition? definition = null)
    {
        Root = root;
        Definition = definition ?? ClipModelDefinition.ChineseClipVitB16;
    }

    public ClipModelDefinition Definition { get; }

    public string Root { get; }

    /// <summary>约定 Files 的第一个文件为模型，第二个文件为词表。</summary>
    public string ModelPath => Path.Combine(Root, Definition.Files[0].LocalPath);

    public string VocabPath => Path.Combine(Root, Definition.Files[1].LocalPath);

    /// <summary>所有文件都存在且字节数与模型定义一致。</summary>
    public bool IsReady => ModelDownloader.IsReady(Root, ToModelFiles());

    /// <summary>
    /// 下载缺失的模型文件；单个文件依次尝试各下载源，全部失败抛出异常。
    /// </summary>
    public Task DownloadAsync(
        IProgress<ClipModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var adapter = progress is null ? null : new ProgressAdapter(progress);
        return ModelDownloader.DownloadAsync(
            Root, ToModelFiles(), Definition.BaseUrls, Http, adapter, ct);
    }

    /// <summary>删除本地模型文件；调用前需释放推理会话。</summary>
    public void Remove() => ModelDownloader.DeleteFiles(Root, ToModelFiles());

    private IReadOnlyList<ModelFile> ToModelFiles() =>
        Definition.Files
            .Select(file => new ModelFile(file.RemotePath, file.LocalPath, file.ExpectedBytes))
            .ToList();

    private sealed class ProgressAdapter : IProgress<ModelDownloadProgress>
    {
        private readonly IProgress<ClipModelDownloadProgress> _target;

        public ProgressAdapter(IProgress<ClipModelDownloadProgress> target) => _target = target;

        public void Report(ModelDownloadProgress value) =>
            _target.Report(new ClipModelDownloadProgress(
                value.FileName, value.Downloaded, value.Total));
    }
}
