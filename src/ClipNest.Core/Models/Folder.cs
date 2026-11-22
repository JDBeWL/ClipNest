namespace ClipNest.Core.Models;

public sealed class Folder
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public bool WatchEnabled { get; set; } = true;

    /// <summary>该文件夹使用的标注模型 ID；为空时跟随默认模型。</summary>
    public string TagModelId { get; set; } = "";

    public DateTime? LastScanAt { get; set; }
    public int AssetCount { get; set; }

    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('/', '\\');
            var index = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            return index >= 0 ? trimmed[(index + 1)..] : trimmed;
        }
    }
}
