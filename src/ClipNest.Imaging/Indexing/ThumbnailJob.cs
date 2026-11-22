namespace ClipNest.Imaging.Indexing;

public sealed record ThumbnailJob(
    long AssetId,
    string Sha256,
    string SourcePath,
    string Extension,
    int Size);
