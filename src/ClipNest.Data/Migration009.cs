namespace ClipNest.Data;

/// <summary>
/// 迁移 009，把存量图片的宽高换算成显示方向。
/// 早期按 EXIF 原始像素记录尺寸，90 度与 270 度的图片在图库里横竖颠倒；
/// 扫描器写入的是旋转后的尺寸，旧记录在此互换一次。
/// </summary>
public static class Migration009
{
    public const string Sql = """
        UPDATE assets
        SET width = height,
            height = width
        WHERE orientation IN (5, 6, 7, 8)
          AND width IS NOT NULL
          AND height IS NOT NULL;
        """;
}
