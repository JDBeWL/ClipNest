using ClipNest.Core.Models;
using ClipNest.Data;
using ClipNest.Data.Repositories;
using ClipNest.Imaging.Decoding;
using ClipNest.Imaging.Thumbnails;
using SkiaSharp;

namespace ClipNest.Imaging.Tests;

public sealed class ThumbnailServiceTests
{
    [Fact]
    public async Task Creates_Webp_Cache_And_Reuses_It()
    {
        var (root, service, assetId, source) = await CreateAsync();
        try
        {
            var path1 = await service.GetOrCreateAsync(assetId, "abc123", source, ".png", 128);
            var path2 = await service.GetOrCreateAsync(assetId, "abc123", source, ".png", 128);

            Assert.NotNull(path1);
            Assert.Equal(path1, path2);
            Assert.True(File.Exists(path1));

            using var thumb = SKBitmap.Decode(path1);
            Assert.True(thumb.Width <= 128);
            Assert.True(thumb.Height <= 128);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetOrCreate_DoesNotPostBackToCallerContext()
    {
        var (root, service, assetId, source) = await CreateAsync();
        try
        {
            string? path = null;
            var posts = 0;

            // 模拟 UI 线程。任何回到调用方上下文的续体都会被计数。
            // 编码与落盘必须全部在线程池上完成，否则首次浏览时主线程会被逐张卡住。
            var thread = new Thread(() =>
            {
                var context = new CountingSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    path = service
                        .GetOrCreateAsync(assetId, "abc123", source, ".png", 128)
                        .GetAwaiter().GetResult();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    posts = context.PostCount;
                }
            });
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "解码线程未在 30 秒内结束");
            Assert.NotNull(path);
            Assert.Equal(0, posts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _posts;

        public int PostCount => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    private static async Task<(string Root, ThumbnailService Service, long AssetId, string Source)>
        CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipnest-thumb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var source = Path.Combine(root, "source.png");
        using (var bitmap = new SKBitmap(200, 100))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        await using (var stream = File.Create(source))
        {
            data.SaveTo(stream);
        }

        var db = new ClipNestDatabase(Path.Combine(root, "test.db"));
        await db.InitializeAsync();
        var folderId = await new FolderRepository(db).EnsureAsync(root);
        var assetId = await new AssetRepository(db).UpsertAsync(
            new Asset
            {
                Sha256 = "abc123", FileName = "source.png",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            },
            new AssetFile
            {
                FolderId = folderId, Path = source, FileName = "source.png", Extension = ".png",
                SizeBytes = 1, ModifiedAt = DateTime.UtcNow, IsPrimary = true
            });

        var service = new ThumbnailService(
            new IImageDecoder[] { new SkiaImageDecoder() },
            new ThumbnailCache(Path.Combine(root, "thumbs")),
            new ThumbnailRepository(db));

        return (root, service, assetId, source);
    }
}
