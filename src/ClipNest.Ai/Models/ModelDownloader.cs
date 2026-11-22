using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace ClipNest.Ai.Models;

/// <summary>
/// 模型文件描述，含远端相对路径、本地文件名与预期字节数。
/// </summary>
public sealed record ModelFile(string RemotePath, string LocalPath, long ExpectedBytes);

/// <summary>
/// 模型下载进度。多个文件按顺序下载，<see cref="FileIndex"/> 从 1 开始。
/// </summary>
public sealed record ModelDownloadProgress(
    string FileName,
    long Downloaded,
    long Total,
    int FileIndex = 1,
    int FileCount = 1);

/// <summary>
/// 下载并校验 ONNX 模型文件。各模型共用一套逻辑，写入 .tmp 后原子替换，避免半截文件被读到。
/// </summary>
public static class ModelDownloader
{
    /// <summary>分片数量。模型文件多为数百 MB，分片能绕过单连接限速。</summary>
    public const int DefaultSegments = 6;

    private const int BufferSize = 64 * 1024;
    private const int MinSegmentBytes = 256 * 1024;

    public static bool IsReady(string root, IReadOnlyList<ModelFile> files)
        => files.All(file =>
        {
            var path = Path.Combine(root, file.LocalPath);
            return File.Exists(path) && new FileInfo(path).Length == file.ExpectedBytes;
        });

    public static async Task DownloadAsync(
        string root,
        IReadOnlyList<ModelFile> files,
        IReadOnlyList<string> baseUrls,
        HttpClient http,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(root);

        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var file = files[fileIndex];
            var localPath = Path.Combine(root, file.LocalPath);
            if (File.Exists(localPath) && new FileInfo(localPath).Length == file.ExpectedBytes)
            {
                continue;
            }

            Exception? lastError = null;
            var downloaded = false;

            foreach (var baseUrl in baseUrls)
            {
                ct.ThrowIfCancellationRequested();
                var tempPath = localPath + ".tmp";
                try
                {
                    await DownloadFileAsync(
                            http, baseUrl + file.RemotePath, tempPath, file,
                            progress, fileIndex + 1, files.Count, ct)
                        .ConfigureAwait(false);
                    File.Move(tempPath, localPath, overwrite: true);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(tempPath);
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TryDelete(tempPath);
                }
            }

            if (!downloaded)
            {
                throw new InvalidOperationException($"模型文件下载失败：{file.LocalPath}", lastError);
            }
        }
    }

    /// <summary>删除模型文件；调用前需先释放占用文件的推理会话。</summary>
    public static void DeleteFiles(string root, IReadOnlyList<ModelFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.LocalPath);
            TryDelete(path);
            TryDelete(path + ".tmp");
        }

        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            try
            {
                Directory.Delete(root);
            }
            catch (IOException)
            {
                // 目录暂时被占用时保留空目录，不影响后续重新下载。
            }
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient http,
        string url,
        string tempPath,
        ModelFile file,
        IProgress<ModelDownloadProgress>? progress,
        int fileIndex,
        int fileCount,
        CancellationToken ct)
    {
        var total = await GetLengthAsync(http, url, file.ExpectedBytes, ct).ConfigureAwait(false);

        if (total > 0 && await SupportsRangesAsync(http, url, ct).ConfigureAwait(false))
        {
            var segments = (int)Math.Min(DefaultSegments, total / MinSegmentBytes);
            if (segments > 1)
            {
                await DownloadSegmentedAsync(
                        http, url, tempPath, file, total, segments,
                        progress, fileIndex, fileCount, ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        await DownloadSingleAsync(
                http, url, tempPath, file, total,
                progress, fileIndex, fileCount, ct)
            .ConfigureAwait(false);
    }

    private static async Task DownloadSingleAsync(
        HttpClient http,
        string url,
        string tempPath,
        ModelFile file,
        long total,
        IProgress<ModelDownloadProgress>? progress,
        int fileIndex,
        int fileCount,
        CancellationToken ct)
    {
        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var expected = total > 0 ? total : file.ExpectedBytes;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long received = 0;
        var reporter = new ProgressReporter(file, expected, fileIndex, fileCount, progress);
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            reporter.Report(received);
        }

        reporter.Report(received, force: true);

        // 连接提前关闭时 ReadAsync 会正常返回 0，必须核对字节数，避免把半截文件当成完整权重。
        if (expected > 0 && received != expected)
        {
            throw new IOException(
                $"模型文件不完整：{file.LocalPath}，已接收 {received}/{expected} 字节");
        }
    }

    /// <summary>
    /// 分段并行下载。各分片按字节区间写入同一个临时文件，再原子改名；
    /// 服务器不支持 Range 时由调用方退回单连接。
    /// </summary>
    private static async Task DownloadSegmentedAsync(
        HttpClient http,
        string url,
        string tempPath,
        ModelFile file,
        long total,
        int segments,
        IProgress<ModelDownloadProgress>? progress,
        int fileIndex,
        int fileCount,
        CancellationToken ct)
    {
        var segmentBytes = total / segments;
        var reporter = new ProgressReporter(file, total, fileIndex, fileCount, progress);
        long received = 0;

        using var handle = File.OpenHandle(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        RandomAccess.SetLength(handle, total);

        var tasks = new List<Task>(segments);
        for (var i = 0; i < segments; i++)
        {
            var start = i * segmentBytes;
            var end = i == segments - 1 ? total - 1 : start + segmentBytes - 1;
            var offset = start;
            tasks.Add(Task.Run(async () =>
            {
                await DownloadSegmentAsync(
                        http, url, handle, offset, start, end, BufferSize, ct,
                        count => reporter.Report(Interlocked.Add(ref received, count)))
                    .ConfigureAwait(false);
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        reporter.Report(Interlocked.Read(ref received), force: true);

        if (Interlocked.Read(ref received) != total)
        {
            throw new IOException(
                $"模型文件不完整：{file.LocalPath}，已接收 {received}/{total} 字节");
        }
    }

    private static async Task DownloadSegmentAsync(
        HttpClient http,
        string url,
        SafeFileHandle handle,
        long offset,
        long start,
        long end,
        int bufferSize,
        CancellationToken ct,
        Action<int> onRead)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new IOException($"服务器未按区间返回：{response.StatusCode}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[bufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            var remaining = end + 1 - offset;
            if (remaining <= 0) break;

            var count = (int)Math.Min(read, remaining);
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, count), offset, ct)
                .ConfigureAwait(false);
            offset += count;
            onRead(count);
        }
    }

    private static async Task<long> GetLengthAsync(
        HttpClient http, string url, long fallback, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var length = response.Content.Headers.ContentLength;
            if (length is > 0) return length.Value;
        }
        catch (HttpRequestException)
        {
            // 部分镜像不支持 HEAD，退回配置里的预期字节数。
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时同样退回预期字节数。
        }

        return fallback;
    }

    private static async Task<bool> SupportsRangesAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange?.HasRange == true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响后续流程。
        }
    }

    /// <summary>多个分片共用一个进度回调，按时间节流，避免每个 64KB 都向 UI 线程投递。</summary>
    private sealed class ProgressReporter
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(120);

        private readonly ModelFile _file;
        private readonly long _total;
        private readonly int _fileIndex;
        private readonly int _fileCount;
        private readonly IProgress<ModelDownloadProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _lastReportMs = -1000;

        public ProgressReporter(
            ModelFile file,
            long total,
            int fileIndex,
            int fileCount,
            IProgress<ModelDownloadProgress>? progress)
        {
            _file = file;
            _total = total;
            _fileIndex = fileIndex;
            _fileCount = fileCount;
            _progress = progress;
        }

        public void Report(long received, bool force = false)
        {
            if (_progress is null) return;

            var now = _clock.ElapsedMilliseconds;
            if (!force && now - _lastReportMs < Interval.TotalMilliseconds) return;

            _lastReportMs = now;
            _progress.Report(new ModelDownloadProgress(
                _file.LocalPath, received, _total, _fileIndex, _fileCount));
        }
    }
}
