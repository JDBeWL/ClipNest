using System.Threading.Channels;
using ClipNest.Imaging.Thumbnails;

namespace ClipNest.Imaging.Indexing;

public sealed class ThumbnailQueue
{
    private readonly Channel<ThumbnailJob> _visible = Channel.CreateUnbounded<ThumbnailJob>();
    private readonly Channel<ThumbnailJob> _background = Channel.CreateUnbounded<ThumbnailJob>();
    private readonly IThumbnailPipeline _pipeline;
    private volatile bool _paused;

    public ThumbnailQueue(IThumbnailPipeline pipeline) => _pipeline = pipeline;

    public void Enqueue(ThumbnailJob job, bool visible)
        => (visible ? _visible : _background).Writer.TryWrite(job);

    public bool TryDequeue(out ThumbnailJob? job)
    {
        if (_visible.Reader.TryRead(out job)) return true;
        if (_background.Reader.TryRead(out job)) return true;
        job = null;
        return false;
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            while (_paused)
                await Task.Delay(100, ct);

            if (!TryDequeue(out var job) || job is null)
            {
                await Task.Delay(50, ct);
                continue;
            }

            await _pipeline.GetOrCreateAsync(
                job.AssetId, job.Sha256, job.SourcePath, job.Extension, job.Size, ct);
        }
    }
}
