using Avalonia.Media.Imaging;
using ClipNest.Core.Models;

namespace ClipNest.App.Services;

public interface IThumbnailProvider
{
    Task<Bitmap?> GetAsync(AssetListItem item, int size, CancellationToken ct = default);

    /// <summary>
    /// 取内存里已经解码好的任意档位。查看器打开时先铺这张图，
    /// 等目标尺寸解码完成再无声替换，避免先白屏再出现画面。
    /// </summary>
    Bitmap? GetCached(string sha256) => null;

    /// <summary>光墙景深用的预模糊小图；默认实现退化为普通缩略图。</summary>
    Task<Bitmap?> GetBlurredAsync(AssetListItem item, int size = 32, CancellationToken ct = default)
        => GetAsync(item, size, ct);
}
