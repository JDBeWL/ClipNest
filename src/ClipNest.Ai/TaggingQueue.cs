using ClipNest.Ai.Tagging;
using ClipNest.Core.Tagging;
using ClipNest.Data.Repositories;

namespace ClipNest.Ai;

/// <summary>
/// 标注队列进度。
/// </summary>
public sealed record TaggingProgress(int Done, int Pending, string? CurrentFile);

/// <summary>
/// 后台标注队列，按文件夹指定的模型逐张识别并持久化。
/// 模型缺失时跳过该模型的任务，不拖慢启动。
/// </summary>
public sealed class TaggingQueue : IDisposable
{
    private const int BatchSize = 6;
    private const int IdleWaitMs = 4000;

    private readonly AiTagRepository _repository;
    private readonly TagModelCatalog _catalog;
    private readonly Func<string> _configuredDefaultModelId;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private bool _disposed;
    private volatile bool _paused;
    private volatile bool _forceRun;
    private volatile bool _resetting;

    public TaggingQueue(
        AiTagRepository repository,
        TagModelCatalog catalog,
        Func<string> configuredDefaultModelId)
    {
        _repository = repository;
        _catalog = catalog;
        _configuredDefaultModelId = configuredDefaultModelId;
    }

    public event EventHandler<TaggingProgress>? ProgressChanged;

    /// <summary>暂停后不再领取新任务；正在处理的一张会完成，恢复时由调用方唤醒。</summary>
    public bool IsPaused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value) _forceRun = false;
        }
    }

    private string DefaultModelId => _catalog.Resolve(_configuredDefaultModelId()).Definition.Id;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有未消费的信号，忽略。
        }
    }

    /// <summary>
    /// 清空全部模型的识别结果并重新排队；手动修改的角色与作品保留。
    /// 自动识别关闭时也会执行，直到这一批处理完或用户再次暂停。
    /// </summary>
    public async Task ReidentifyAllAsync(CancellationToken ct = default)
    {
        _resetting = true;
        try
        {
            await _repository.ResetAllAsync(ct);
            _forceRun = true;
        }
        finally
        {
            _resetting = false;
        }

        Signal();
    }

    /// <summary>
    /// 清空单张图片的识别结果并重新排队；手动修改的角色与作品保留。
    /// </summary>
    public async Task ReidentifyAssetAsync(long assetId, CancellationToken ct = default)
    {
        _resetting = true;
        try
        {
            await _repository.ResetForAssetAsync(assetId, ct);
            _forceRun = true;
        }
        finally
        {
            _resetting = false;
        }

        Signal();
    }

    /// <summary>读取图片所属文件夹的模型；未指定时返回默认模型。</summary>
    public Task<string?> GetModelIdForAssetAsync(long assetId, CancellationToken ct = default) =>
        _repository.GetModelIdForAssetAsync(assetId, DefaultModelId, ct);

    /// <summary>
    /// 释放全部模型的推理会话，使线程数等设置在下一次识别时生效。
    /// 正在处理的一张会先完成，随后按新设置重建会话。
    /// </summary>
    public async Task ReleaseSessionsAsync(CancellationToken ct = default)
    {
        foreach (var entry in _catalog.Models)
        {
            await entry.Tagger.ReleaseIfIdleAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 清空指定文件夹的识别结果并重新排队；手动修改的角色与作品保留。
    /// </summary>
    public async Task ReidentifyFolderAsync(long folderId, CancellationToken ct = default)
    {
        _resetting = true;
        try
        {
            await _repository.ResetForFolderAsync(folderId, ct);
            _forceRun = true;
        }
        finally
        {
            _resetting = false;
        }

        Signal();
    }

    /// <summary>处理一轮待识别资产；没有任务时立即返回。</summary>
    public async Task ProcessPendingAsync(int batchSize = BatchSize, CancellationToken ct = default)
    {
        if (_paused && !_forceRun)
        {
            return;
        }

        var processed = false;
        foreach (var entry in _catalog.Models)
        {
            if (_paused && !_forceRun)
            {
                return;
            }

            if (!entry.Tagger.IsAvailable)
            {
                continue;
            }

            if (await ProcessModelAsync(entry, batchSize, ct).ConfigureAwait(false))
            {
                processed = true;
            }
        }

        if (!_resetting && !processed)
        {
            _forceRun = false;
        }

        await ReportAsync(null, ct).ConfigureAwait(false);
    }

    public Task<AiTagStats> GetStatsAsync(CancellationToken ct = default) =>
        _repository.GetStatsAsync(DefaultModelId, ct);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }

    private async Task<bool> ProcessModelAsync(
        TagModelEntry entry,
        int batchSize,
        CancellationToken ct)
    {
        var afterAssetId = 0L;
        var processed = false;

        while (true)
        {
            var jobs = await _repository
                .GetPendingAsync(entry.Definition.Id, DefaultModelId, batchSize, afterAssetId, ct)
                .ConfigureAwait(false);
            if (jobs.Count == 0)
            {
                return processed;
            }

            processed = true;
            for (var index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                if (_paused && !_forceRun)
                {
                    return processed;
                }

                ct.ThrowIfCancellationRequested();

                // 游标先于处理前移。即使当前图片失败也不原地重试，否则整批待处理资产会停滞。
                afterAssetId = job.AssetId;

                // 先启动下一张的解码，与当前图片的推理重叠。
                if (entry.Tagger is ITagPrefetcher prefetcher && index + 1 < jobs.Count)
                {
                    prefetcher.Prefetch(jobs[index + 1].Path);
                }

                try
                {
                    var result = await entry.Tagger.TagAsync(job.Path, ct).ConfigureAwait(false);
                    await _repository.ReplaceAsync(job.AssetId, entry.Definition.Id, result, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单张失败不阻塞其余任务，下一次队列轮询会重新取到它。
                }

                await ReportAsync(job.FileName, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_catalog.Models.Any(entry => entry.Tagger.IsAvailable))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    continue;
                }

                await ProcessPendingAsync(BatchSize, ct).ConfigureAwait(false);

                // 没有待标注任务时逐步释放会话。模型常驻不划算，下次标注重新加载约需 2 秒。
                foreach (var entry in _catalog.Models)
                {
                    await entry.Tagger.ReleaseIfIdleAsync(TimeSpan.FromMinutes(5), ct)
                        .ConfigureAwait(false);
                }

                // 没有待处理任务时等待新信号，超时后重新检查是否有新导入。
                await _signal.WaitAsync(IdleWaitMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 队列自身不因单次异常退出。
                try
                {
                    await Task.Delay(IdleWaitMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ReportAsync(string? currentFile, CancellationToken ct)
    {
        var handler = ProgressChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            var stats = await _repository.GetStatsAsync(DefaultModelId, ct).ConfigureAwait(false);
            handler(this, new TaggingProgress(stats.Done, stats.Pending, currentFile));
        }
        catch
        {
            // 统计失败不影响标注本身。
        }
    }
}
