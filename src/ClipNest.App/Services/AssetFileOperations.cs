using ClipNest.Core.Models;
using ClipNest.Data.Repositories;

namespace ClipNest.App.Services;

/// <summary>批量文件操作的两种去向。</summary>
public enum FileTransferMode
{
    Copy,
    Move
}

/// <summary>一批文件操作的结果。失败项保留原因，供状态栏提示。</summary>
public sealed record BatchOutcome(int Succeeded, int Skipped, IReadOnlyList<string> Errors)
{
    public static BatchOutcome None { get; } = new(0, 0, Array.Empty<string>());

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>把文件移出磁盘的策略。回收站不可用的卷需要由实现退回直接删除。</summary>
public interface IFileRemover
{
    /// <summary>移除给定文件，返回处理后已不存在的路径。仍然存在的路径视为失败。</summary>
    IReadOnlyList<string> Remove(IReadOnlyList<string> paths);
}

/// <summary>
/// 批量复制、移动与删除图片。磁盘动作与索引写入在同一个方法内完成，
/// 调用方只需在成功后刷新集合。
/// </summary>
public sealed class AssetFileOperations
{
    private readonly FolderRepository _folders;
    private readonly AssetRepository _assets;
    private readonly IFileRemover _remover;

    public AssetFileOperations(
        FolderRepository folders, AssetRepository assets, IFileRemover? remover = null)
    {
        _folders = folders;
        _assets = assets;
        _remover = remover ?? new RecycleBin();
    }

    /// <summary>
    /// 把每张图片的主文件复制或移动到目标文件夹。目标文件夹未在图库中时一并加入，
    /// 否则移动后的图片会从图库里消失。
    /// </summary>
    public Task<BatchOutcome> TransferAsync(
        IReadOnlyList<AssetListItem> items,
        string targetFolderPath,
        FileTransferMode mode,
        CancellationToken ct = default)
    {
        // 磁盘复制与移动是同步调用，放到线程池执行，批次很大时不会冻结界面。
        return Task.Run(() => TransferCoreAsync(items, targetFolderPath, mode, ct), ct);
    }

    private async Task<BatchOutcome> TransferCoreAsync(
        IReadOnlyList<AssetListItem> items,
        string targetFolderPath,
        FileTransferMode mode,
        CancellationToken ct)
    {
        if (items.Count == 0) return BatchOutcome.None;

        string targetFolder;
        try
        {
            targetFolder = Path.GetFullPath(targetFolderPath);
        }
        catch (Exception ex)
        {
            return new BatchOutcome(0, items.Count,
                new[] { AppLocalization.Format("FileOp.InvalidTargetPath", ex.Message) });
        }

        if (!Directory.Exists(targetFolder))
            return new BatchOutcome(0, items.Count,
                new[] { AppLocalization.Format("FileOp.TargetNotFound", targetFolder) });

        var folderId = await _folders.EnsureAsync(targetFolder, ct);
        var files = await _assets.GetFilesForAssetsAsync(items.Select(item => item.Id).ToList(), ct);
        var filesByAsset = files.GroupBy(file => file.AssetId)
            .ToDictionary(group => group.Key, group => group.ToList());

        // 索引里已占用的路径不得选用，asset_files.path 带唯一约束。
        var reserved = new HashSet<string>(
            await _assets.GetIndexedPathsAsync(files.Select(file => file.Path).ToList(), ct),
            StringComparer.OrdinalIgnoreCase);

        var relocations = new List<FileRelocation>(items.Count);
        var additions = new List<FileAddition>(items.Count);
        var errors = new List<string>();
        var succeeded = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (!filesByAsset.TryGetValue(item.Id, out var assetFiles) || assetFiles.Count == 0)
            {
                errors.Add(AppLocalization.Format("FileOp.NotInIndex", item.FileName));
                skipped++;
                continue;
            }

            // 主文件缺失时回退到任意一个仍在磁盘上的副本，避免因外部移动文件导致整批失败。
            var source = assetFiles.FirstOrDefault(file => file.IsPrimary && File.Exists(file.Path))
                ?? assetFiles.FirstOrDefault(file => File.Exists(file.Path))
                ?? assetFiles[0];
            if (!File.Exists(source.Path))
            {
                errors.Add(AppLocalization.Format("FileOp.FileNotFound", item.FileName));
                skipped++;
                continue;
            }

            if (mode == FileTransferMode.Move && IsInFolder(source.Path, targetFolder))
            {
                skipped++;
                continue;
            }

            var targetPath = MakeUniquePath(targetFolder, Path.GetFileName(source.Path), reserved);
            reserved.Add(targetPath);

            try
            {
                if (mode == FileTransferMode.Move)
                    MoveFile(source.Path, targetPath);
                else
                    File.Copy(source.Path, targetPath);
            }
            catch (Exception ex)
            {
                errors.Add(AppLocalization.Format("FileOp.ErrorItem", item.FileName, ex.Message));
                continue;
            }

            var info = new FileInfo(targetPath);
            if (mode == FileTransferMode.Move)
            {
                relocations.Add(new FileRelocation(
                    source.Id,
                    source.AssetId,
                    folderId,
                    targetPath,
                    info.Name,
                    info.Extension.ToLowerInvariant(),
                    info.Length,
                    info.LastWriteTimeUtc));
            }
            else
            {
                additions.Add(new FileAddition(
                    source.AssetId,
                    folderId,
                    targetPath,
                    info.Name,
                    info.Extension.ToLowerInvariant(),
                    info.Length,
                    info.LastWriteTimeUtc));
            }

            succeeded++;
        }

        try
        {
            await _assets.RelocateFilesAsync(relocations, ct);
            await _assets.AddFileCopiesAsync(additions, ct);
        }
        catch (Exception ex)
        {
            // 文件已迁移而索引写入失败时如实报错，下一次扫描会重新登记这条路径。
            errors.Add(AppLocalization.Format("FileOp.IndexWriteFailed", ex.Message));
        }

        return new BatchOutcome(succeeded, skipped, errors);
    }

    /// <summary>
    /// 删除选中图片的全部文件与索引。资产以内容为单位，
    /// 同一张图片的多个副本会一起移入回收站。
    /// </summary>
    public async Task<BatchOutcome> DeleteAsync(
        IReadOnlyList<AssetListItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return BatchOutcome.None;

        var files = await _assets.GetFilesForAssetsAsync(items.Select(item => item.Id).ToList(), ct);
        var filesByAsset = files.GroupBy(file => file.AssetId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var existing = files
            .Where(file => File.Exists(file.Path))
            .Select(file => file.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removed = new HashSet<string>(_remover.Remove(existing), StringComparer.OrdinalIgnoreCase);

        var fileIds = new List<long>(files.Count);
        var errors = new List<string>();
        var deleted = 0;

        foreach (var item in items)
        {
            if (!filesByAsset.TryGetValue(item.Id, out var assetFiles) || assetFiles.Count == 0)
            {
                errors.Add(AppLocalization.Format("FileOp.NotInIndex", item.FileName));
                continue;
            }

            if (assetFiles.Any(file => File.Exists(file.Path) && !removed.Contains(file.Path)))
            {
                errors.Add(AppLocalization.Format("FileOp.FileInUse", item.FileName));
                continue;
            }

            fileIds.AddRange(assetFiles.Select(file => file.Id));
            deleted++;
        }

        await _assets.DeleteFilesAsync(fileIds, ct);
        await _assets.PruneOrphansAsync(ct);

        return new BatchOutcome(deleted, items.Count - deleted, errors);
    }

    /// <summary>
    /// 在目标文件夹里挑一个不冲突的文件名。重名时追加序号，
    /// 已在磁盘或索引中占用的路径都会被跳过。
    /// </summary>
    internal static string MakeUniquePath(
        string folder, string fileName, IReadOnlySet<string> reserved)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !reserved.Contains(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 1000; index++)
        {
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !reserved.Contains(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static bool IsInFolder(string path, string folder)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return false;

        return string.Equals(
            parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>跨卷时 File.Move 会失败，改为复制后删除源文件。</summary>
    private static void MoveFile(string source, string target)
    {
        try
        {
            File.Move(source, target);
        }
        catch (IOException) when (DiffersByVolume(source, target))
        {
            File.Copy(source, target);
            File.Delete(source);
        }
    }

    private static bool DiffersByVolume(string first, string second)
    {
        var firstRoot = Path.GetPathRoot(first);
        var secondRoot = Path.GetPathRoot(second);
        if (string.IsNullOrEmpty(firstRoot) || string.IsNullOrEmpty(secondRoot)) return false;
        return !string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
    }
}
