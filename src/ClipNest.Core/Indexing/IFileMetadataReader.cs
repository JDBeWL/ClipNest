namespace ClipNest.Core.Indexing;

public interface IFileMetadataReader
{
    Task<AssetMetadata> ReadAsync(string path, CancellationToken ct = default);
}
