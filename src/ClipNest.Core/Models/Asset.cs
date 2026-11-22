namespace ClipNest.Core.Models;

public enum DecodeStatus { Pending, Ready, Unsupported, CodecMissing, Corrupt }
public enum EmbeddingStatus { Pending, Ready, Error, Skipped }
public enum FileState { Indexed, Missing, Error }

public sealed class Asset
{
    public long Id { get; set; }
    public string Sha256 { get; set; } = "";
    public string FileName { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Orientation { get; set; }
    public DateTime? TakenAt { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? Lens { get; set; }
    public int? Iso { get; set; }
    public double? Aperture { get; set; }
    public string? Shutter { get; set; }
    public double? FocalLength { get; set; }
    public int Rating { get; set; }
    public bool IsFavorite { get; set; }
    public int Flag { get; set; }
    public DecodeStatus DecodeStatus { get; set; } = DecodeStatus.Pending;
    public EmbeddingStatus EmbeddingStatus { get; set; } = EmbeddingStatus.Pending;
    public string Notes { get; set; } = "";
    public string OcrText { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
