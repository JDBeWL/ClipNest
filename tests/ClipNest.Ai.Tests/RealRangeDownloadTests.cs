using ClipNest.Ai.Models;

namespace ClipNest.Ai.Tests;

/// <summary>
/// 真实的 Range 下载冒烟测试。设置 CLIPNEST_REAL_DOWNLOAD=1 后运行；默认跳过。
/// </summary>
public sealed class RealRangeDownloadTests
{
    [Fact]
    public async Task DownloadsRealFileWithRanges()
    {
        if (Environment.GetEnvironmentVariable("CLIPNEST_REAL_DOWNLOAD") != "1") return;

        const string url = "https://hf-mirror.com/Camais03/camie-tagger-v2/resolve/main/camie-tagger-v2-metadata.json";
        const long size = 7_771_946;
        var directory = Path.Combine(Path.GetTempPath(), $"clipnest-real-{Guid.NewGuid():N}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var file = new ModelFile("camie-tagger-v2-metadata.json", "meta.json", size);

            await ModelDownloader.DownloadAsync(
                directory, new[] { file }, new[] { url[..(url.LastIndexOf('/') + 1)] }, http,
                null, CancellationToken.None);

            var path = Path.Combine(directory, file.LocalPath);
            Assert.Equal(size, new FileInfo(path).Length);
            Assert.Equal('{', File.ReadAllText(path)[0]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
