namespace ClipNest.Imaging.Thumbnails;

public sealed class ThumbnailCache
{
    private readonly string _root;

    public ThumbnailCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string GetPath(string sha256, int size)
    {
        var prefix = sha256.Length >= 2 ? sha256[..2] : "00";
        var directory = Path.Combine(_root, prefix);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{sha256}_{size}.webp");
    }
}
