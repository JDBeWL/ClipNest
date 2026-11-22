using System.Net;
using System.Net.Http.Headers;
using ClipNest.Ai.Models;

namespace ClipNest.Ai.Tests;

public sealed class ModelDownloaderTests
{
    [Fact]
    public async Task SegmentedDownload_WritesCompleteFile()
    {
        var payload = new byte[1024 * 1024 + 123];
        Random.Shared.NextBytes(payload);
        var directory = Path.Combine(Path.GetTempPath(), $"clipnest-download-{Guid.NewGuid():N}");
        try
        {
            using var http = new HttpClient(new RangeHandler(payload));
            var file = new ModelFile("model.bin", "model.bin", payload.Length);

            await ModelDownloader.DownloadAsync(
                directory, new[] { file }, new[] { "https://test/" }, http, null, CancellationToken.None);

            var downloaded = await File.ReadAllBytesAsync(Path.Combine(directory, "model.bin"));
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancel_RemovesTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipnest-download-{Guid.NewGuid():N}");
        try
        {
            using var http = new HttpClient(new DelayedHandler());
            var file = new ModelFile("model.bin", "model.bin", 8 * 1024 * 1024);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ModelDownloader.DownloadAsync(
                    directory, new[] { file }, new[] { "https://test/" }, http, null, cts.Token));

            Assert.False(File.Exists(Path.Combine(directory, "model.bin.tmp")));
            Assert.False(File.Exists(Path.Combine(directory, "model.bin")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeleteFiles_RemovesModelAndEmptyDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"clipnest-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = new ModelFile("model.bin", "model.bin", 4);
        File.WriteAllBytes(Path.Combine(directory, file.LocalPath), new byte[] { 1, 2, 3, 4 });

        ModelDownloader.DeleteFiles(directory, new[] { file });

        Assert.False(Directory.Exists(directory));
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public RangeHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                head.Content.Headers.ContentLength = _payload.Length;
                return Task.FromResult(head);
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From is { } from && range.To is { } to)
            {
                var length = (int)Math.Min(to - from + 1, _payload.Length - from);
                var content = new ByteArrayContent(_payload, (int)from, length);
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _payload.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = content
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
