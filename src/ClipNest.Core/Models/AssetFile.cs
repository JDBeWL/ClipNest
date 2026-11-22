namespace ClipNest.Core.Models;

public sealed class AssetFile
{
    public long Id { get; set; }
    public long AssetId { get; set; }
    public long FolderId { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsPrimary { get; set; }
    public FileState State { get; set; } = FileState.Indexed;
    public string? ErrorMessage { get; set; }
}
