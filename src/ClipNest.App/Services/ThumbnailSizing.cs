namespace ClipNest.App.Services;

/// <summary>
/// 缩略图请求尺寸。缓存按长边分档，档位越细越清晰，但每档都会在磁盘与内存里各留一份，
/// 所以只取若干个固定档位，缩放条每动一格不会生成新文件。
/// </summary>
public static class ThumbnailSizing
{
    private static readonly int[] Steps =
    {
        128, 192, 256, 320, 384, 512, 640, 768, 1024, 1280, 1536, 2048, 2560
    };

    /// <summary>
    /// 按显示框与图片比例计算所需的长边。缩略图由控件中心裁切铺满显示框，
    /// 长图按自身比例换算，避免短边只有几十像素却被拉伸到几百像素。
    /// </summary>
    /// <param name="sourceRatio">图片宽高比；未知时按显示框较长的一边估算。</param>
    /// <param name="scaling">显示缩放比，即逻辑像素到物理像素的倍数。</param>
    /// <param name="max">本视图允许的最大长边，超出后按此值截断。</param>
    public static int ForBox(
        double boxWidth, double boxHeight, double? sourceRatio, double scaling, int max = 2048)
    {
        if (boxWidth <= 0 || boxHeight <= 0) return 0;

        var required = RequiredLongSide(boxWidth, boxHeight, sourceRatio) * Math.Max(1, scaling);
        return Bucket(required, max);
    }

    /// <summary>向上取到最近的档位，并截断在 <paramref name="max"/> 以内。</summary>
    public static int Bucket(double pixels, int max = 2048)
    {
        if (double.IsNaN(pixels) || pixels <= 0) return Steps[0];

        var target = Math.Min(pixels, max);
        foreach (var step in Steps)
        {
            // max 不一定是档位上的值，取到的档位不能越过它。
            if (step >= target) return Math.Min(step, max);
        }

        return max;
    }

    private static double RequiredLongSide(double boxWidth, double boxHeight, double? sourceRatio)
    {
        if (sourceRatio is not > 0) return Math.Max(boxWidth, boxHeight);

        var ratio = Math.Clamp(sourceRatio.Value, 0.02, 50);
        return ratio >= 1
            ? Math.Max(boxWidth, boxHeight * ratio)
            : Math.Max(boxWidth / ratio, boxHeight);
    }
}
