using ClipNest.Ai.Models;

namespace ClipNest.Ai.Tagging;

/// <summary>
/// 标注模型文件管理，负责检查就绪状态并按需下载。与语义模型各自独立，互不牵连。
/// </summary>
public sealed class TagModelManager
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(60)
    };

    public TagModelManager(string root, TagModelDefinition? definition = null)
    {
        Root = root;
        Definition = definition ?? TagModelDefinition.Camie;
    }

    public TagModelDefinition Definition { get; }

    public string Root { get; }

    public string ModelPath => Definition.ModelPath(Root);

    public string MetadataPath => Definition.MetadataPath(Root);

    /// <summary>所有文件都存在且字节数与模型定义一致。</summary>
    public bool IsReady => ModelDownloader.IsReady(Root, Definition.Files);

    public async Task DownloadAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        await ModelDownloader.DownloadAsync(
                Root, Definition.Files, Definition.BaseUrls, Http, progress, ct)
            .ConfigureAwait(false);

        // 目录中可能残留其他变体的权重，下载完成后一并清理。
        ModelDownloader.DeleteObsolete(Root, Definition.Files);
    }

    /// <summary>删除本地模型文件；调用前需释放推理会话。</summary>
    public void Remove()
    {
        ModelDownloader.DeleteFiles(Root, Definition.Files);
        ModelDownloader.DeleteObsolete(Root, Definition.Files);
    }
}
