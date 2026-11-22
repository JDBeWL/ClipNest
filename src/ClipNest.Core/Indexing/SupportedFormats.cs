namespace ClipNest.Core.Indexing;

public static class SupportedFormats
{
    public static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff"
    };

    public static readonly HashSet<string> Heic = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".avif"
    };

    public static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw"
    };

    public static bool IsSupported(string extension)
        => Common.Contains(extension) || Heic.Contains(extension) || Raw.Contains(extension);
}
