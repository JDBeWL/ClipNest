using ClipNest.Ai.Models;
using ClipNest.Core.Embedding;
using ClipNest.Data.Repositories;

namespace ClipNest.Ai;

/// <summary>
/// 后台索引进度。
/// </summary>
public sealed record EmbeddingProgress(int Ready, int Pending, int Error, int Skipped, string? CurrentFile);

/// <summary>
/// 后台嵌入队列，逐张编码图片并持久化向量；失败重试后标记为错误。
/// </summary>
public sealed class EmbeddingQueue : IDisposable
{
    private const int MaxRetries = 2;
    private const int RetryDelayMs = 200;

    private readonly EmbeddingRepository _repository;
    private readonly IClipEncoder _encoder;
    private readonly ClipModelDefinition _definition;
    private readonly SemaphoreSlim _signal = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private bool _skippedReset;
    private bool _disposed;
    private volatile bool _paused;

    public EmbeddingQueue(EmbeddingRepository repository, IClipEncoder encoder, ClipModelDefinition definition)
    {
        _repository = repository;
        _encoder = encoder;
        _definition = definition;
    }

    public event EventHandler<EmbeddingProgress>? ProgressChanged;

    /// <summary>暂停后不再领取新任务；正在处理的一张会完成，恢复时由调用方唤醒。</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>
    /// 启动后台循环；重复调用无副作用。
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// 唤醒后台循环；重复信号不抛异常。
    /// </summary>
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

    public async Task ProcessPendingAsync(int batchSize = 8, CancellationToken ct = default)
    {
        if (_paused)
        {
            return;
        }

        if (!_encoder.IsAvailable)
        {
            await _repository.MarkStatusAsync("pending", "skipped", ct).ConfigureAwait(false);
            await ReportAsync(null, ct).ConfigureAwait(false);
            return;
        }

        // 模型可用时重新排队上次跳过的任务；每个实例只执行一次。
        if (!_skippedReset)
        {
            _skippedReset = true;
            await ResetSkippedAsync(ct).ConfigureAwait(false);
        }

        while (true)
        {
            var jobs = await _repository.GetPendingAsync(batchSize, ct).ConfigureAwait(false);
            if (jobs.Count == 0)
            {
                return;
            }

            foreach (var job in jobs)
            {
                if (_paused)
                {
                    return;
                }

                ct.ThrowIfCancellationRequested();

                try
                {
                    var vector = await EncodeWithRetryAsync(job.Path, ct).ConfigureAwait(false);
                    await _repository.UpsertAsync(job.AssetId, _definition.Id, vector, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单张失败不阻塞其他任务。
                    await _repository.MarkStatusAsync(job.AssetId, "error", ct).ConfigureAwait(false);
                }

                await ReportAsync(job.FileName, ct).ConfigureAwait(false);
            }
        }
    }

    public Task ResetSkippedAsync(CancellationToken ct = default) =>
        _repository.MarkStatusAsync("skipped", "pending", ct);

    /// <summary>读取当前索引统计。</summary>
    public Task<EmbeddingStats> GetStatsAsync(CancellationToken ct = default) =>
        _repository.GetStatsAsync(ct);

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

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(ct: ct).ConfigureAwait(false);
            }
            catch
            {
                // 后台循环忽略异常，等待下一轮。
            }

            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task<float[]> EncodeWithRetryAsync(string path, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
            }

            try
            {
                return await _encoder.EncodeImageAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last!;
    }

    private async Task ReportAsync(string? currentFile, CancellationToken ct)
    {
        var stats = await _repository.GetStatsAsync(ct).ConfigureAwait(false);
        ProgressChanged?.Invoke(
            this,
            new EmbeddingProgress(stats.Ready, stats.Pending, stats.Error, stats.Skipped, currentFile));
    }
}
