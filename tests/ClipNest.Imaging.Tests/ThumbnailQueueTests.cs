using ClipNest.Imaging.Indexing;
using ClipNest.Imaging.Thumbnails;

namespace ClipNest.Imaging.Tests;

public sealed class ThumbnailQueueTests
{
    private sealed class FakePipeline : IThumbnailPipeline
    {
        public Task<string?> GetOrCreateAsync(
            long assetId, string sha256, string sourcePath, string extension, int size, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    [Fact]
    public void TryDequeue_PrefersVisibleJobs()
    {
        var queue = new ThumbnailQueue(new FakePipeline());
        queue.Enqueue(new ThumbnailJob(1, "a", "a.jpg", ".jpg", 256), visible: false);
        queue.Enqueue(new ThumbnailJob(2, "b", "b.jpg", ".jpg", 256), visible: true);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(2, first!.AssetId);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(1, second!.AssetId);

        Assert.False(queue.TryDequeue(out _));
    }
}
