using System.Security.Cryptography;
using ClipNest.Core.Indexing;
using ClipNest.Core.Models;
using ClipNest.Data.Repositories;

namespace ClipNest.Imaging.Indexing;

/// <summary>一次扫描中已处理与总文件数，用于导入进度。</summary>
public sealed record ScanProgress(int Processed, int Total);

public sealed class FileScanner
{
    private const int ProgressInterval = 8;

    // 哈希与元数据是独立的读操作，并行几路即可；批量写入本身仍在单事务内串行。
    private const int BatchSize = 128;
    private static readonly int IoConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    private sealed record ScanFile(FileInfo Info, string Path);

    private readonly FolderRepository _folders;
    private readonly AssetRepository _assets;
    private readonly IFileMetadataReader _metadata;

    public FileScanner(FolderRepository folders, AssetRepository assets, IFileMetadataReader metadata)
    {
        _folders = folders;
        _assets = assets;
        _metadata = metadata;
    }

    public async Task<int> ScanAsync(
        string folderPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var folderId = await _folders.EnsureAsync(folderPath, ct);
        var known = (await _assets.GetFilesByFolderAsync(folderId, ct))
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

        var root = new DirectoryInfo(folderPath);
        if (!root.Exists)
        {
            // 整个文件夹被删除或移走时，逐条标记缺失；刷新后图库不应继续显示旧图。
            foreach (var existing in known.Values)
            {
                if (existing.State != FileState.Missing)
                {
                    await _assets.MarkMissingAsync(existing.Id, ct);
                }
            }

            await _folders.MarkScannedAsync(folderId, ct);
            await _assets.PruneOrphansAsync(ct);
            return 0;
        }

        var files = new List<ScanFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!SupportedFormats.IsSupported(file.Extension)) continue;

            var fullPath = Path.GetFullPath(file.FullName);
            files.Add(new ScanFile(file, fullPath));
            seen.Add(fullPath);
        }

        progress?.Report(new ScanProgress(0, files.Count));

        var pending = new List<ScanFile>();
        var processed = 0;

        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();
            var file = entry.Info;

            if (known.TryGetValue(entry.Path, out var existing) &&
                existing.SizeBytes == file.Length &&
                existing.ModifiedAt == file.LastWriteTimeUtc)
            {
                if (existing.State == FileState.Missing)
                    await _assets.MarkPresentAsync(existing.Id, ct);
                ReportProgress(progress, ++processed, files.Count);
                continue;
            }

            pending.Add(entry);
        }

        for (var offset = 0; offset < pending.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(BatchSize, pending.Count - offset);
            var upserts = new (Asset Asset, AssetFile File)[count];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = IoConcurrency,
                    CancellationToken = ct
                },
                async (index, token) =>
                {
                    var entry = pending[offset + index];
                    var hash = await ComputeSha256Async(entry.Path, token);
                    var metadata = await _metadata.ReadAsync(entry.Path, token);
                    upserts[index] = (
                        BuildAsset(entry.Info, hash, metadata),
                        BuildAssetFile(folderId, entry));
                });

            await _assets.UpsertManyAsync(upserts, ct);

            processed += count;
            ReportProgress(progress, processed, files.Count);
        }

        foreach (var existing in known.Values)
        {
            if (!seen.Contains(existing.Path) && existing.State != FileState.Missing)
                await _assets.MarkMissingAsync(existing.Id, ct);
        }

        await _folders.MarkScannedAsync(folderId, ct);

        // 被替换掉的文件会留下一条没有路径的旧资产，随后一并清理；
        // 它的语义向量与识别结果会随外键级联删除。
        await _assets.PruneOrphansAsync(ct);

        return pending.Count;
    }

    private static Asset BuildAsset(FileInfo file, string hash, AssetMetadata metadata) => new()
    {
        Sha256 = hash,
        FileName = file.Name,
        Width = metadata.Width,
        Height = metadata.Height,
        Orientation = metadata.Orientation,
        TakenAt = metadata.TakenAt,
        CameraMake = metadata.CameraMake,
        CameraModel = metadata.CameraModel,
        Lens = metadata.Lens,
        Iso = metadata.Iso,
        Aperture = metadata.Aperture,
        Shutter = metadata.Shutter,
        FocalLength = metadata.FocalLength,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AssetFile BuildAssetFile(long folderId, ScanFile entry) => new()
    {
        FolderId = folderId,
        Path = entry.Path,
        FileName = entry.Info.Name,
        Extension = entry.Info.Extension.ToLowerInvariant(),
        SizeBytes = entry.Info.Length,
        ModifiedAt = entry.Info.LastWriteTimeUtc,
        IsPrimary = true
    };

    /// <summary>启动时清理没有路径的孤立资产，避免索引里保留已被替换的旧图。</summary>
    public Task<int> PruneOrphansAsync(CancellationToken ct = default)
        => _assets.PruneOrphansAsync(ct);

    /// <summary>
    /// 补齐旧数据里缺失的宽高。每次启动只读一批，避免一次性遍历整库的元数据。
    /// </summary>
    public async Task<int> BackfillDimensionsAsync(int limit = 200, CancellationToken ct = default)
    {
        var missing = await _assets.GetMissingDimensionsAsync(limit, ct);
        var filled = 0;

        foreach (var entry in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(entry.Path)) continue;

            var metadata = await _metadata.ReadAsync(entry.Path, ct);
            if (metadata.Width is not > 0 || metadata.Height is not > 0) continue;

            await _assets.UpdateDimensionsAsync(
                entry.AssetId, metadata.Width.Value, metadata.Height.Value, metadata.Orientation, ct);
            filled++;
        }

        return filled;
    }

    private static void ReportProgress(
        IProgress<ScanProgress>? progress, int processed, int total)
    {
        if (processed % ProgressInterval == 0 || processed == total)
            progress?.Report(new ScanProgress(processed, total));
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
