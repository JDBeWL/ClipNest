namespace ClipNest.Core.Models;

public sealed class AssetListItem
{
    public long Id { get; init; }
    public string Sha256 { get; init; } = "";
    public string FileName { get; init; } = "";
    public string PrimaryPath { get; init; } = "";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public DateTime? TakenAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public int Rating { get; init; }
    public bool IsFavorite { get; init; }
    public DecodeStatus DecodeStatus { get; init; }
}
