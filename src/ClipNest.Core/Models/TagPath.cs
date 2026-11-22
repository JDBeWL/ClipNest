namespace ClipNest.Core.Models;

public static class TagPath
{
    public static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
