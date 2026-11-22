namespace ClipNest.Core.Indexing;

public sealed record AssetMetadata(
    int? Width,
    int? Height,
    int? Orientation,
    DateTime? TakenAt,
    string? CameraMake,
    string? CameraModel,
    string? Lens,
    int? Iso,
    double? Aperture,
    string? Shutter,
    double? FocalLength)
{
    public static AssetMetadata Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null);
}
