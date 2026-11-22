using ClipNest.Core.Indexing;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SkiaSharp;

namespace ClipNest.Imaging.Metadata;

public sealed class ExifMetadataReader : IFileMetadataReader
{
    public Task<AssetMetadata> ReadAsync(string path, CancellationToken ct = default)
        => Task.Run(() => ReadCore(path), ct);

    private static AssetMetadata ReadCore(string path)
    {
        var metadata = ReadExif(path);
        var width = metadata.Width;
        var height = metadata.Height;
        var orientation = metadata.Orientation;

        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is not null)
            {
                // EXIF 只给 JPEG 的尺寸，PNG、GIF、WebP 等由解码器补齐。
                if (width is not > 0 || height is not > 0)
                {
                    width = codec.Info.Width;
                    height = codec.Info.Height;
                }

                if (orientation is null or 1)
                    orientation = (int)codec.EncodedOrigin;
            }
        }
        catch (Exception)
        {
            // 解码器不支持的容器保留 EXIF 结果。
        }

        // 宽高按显示方向存储，与缩略图、查看器保持一致；90 度与 270 度需要互换。
        if (IsQuarterTurn(orientation) && width is > 0 && height is > 0)
            (width, height) = (height, width);

        return metadata with { Width = width, Height = height, Orientation = orientation };
    }

    private static AssetMetadata ReadExif(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

            DateTime? takenAt = sub?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var value) == true
                ? value
                : null;
            int? orientation = ifd0?.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientationValue) == true
                ? orientationValue
                : null;
            int? iso = sub?.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var isoValue) == true
                ? isoValue
                : null;
            double? aperture = sub?.TryGetDouble(ExifDirectoryBase.TagFNumber, out var apertureValue) == true
                ? apertureValue
                : null;
            double? focalLength = sub?.TryGetDouble(ExifDirectoryBase.TagFocalLength, out var focalValue) == true
                ? focalValue
                : null;

            var jpeg = directories.OfType<MetadataExtractor.Formats.Jpeg.JpegDirectory>().FirstOrDefault();
            int? width = TryGetDimension(jpeg, MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageWidth)
                         ?? TryGetDimension(ifd0, ExifDirectoryBase.TagImageWidth);
            int? height = TryGetDimension(jpeg, MetadataExtractor.Formats.Jpeg.JpegDirectory.TagImageHeight)
                          ?? TryGetDimension(ifd0, ExifDirectoryBase.TagImageHeight);

            return new AssetMetadata(
                Width: width,
                Height: height,
                Orientation: orientation,
                TakenAt: takenAt,
                CameraMake: ifd0?.GetDescription(ExifDirectoryBase.TagMake),
                CameraModel: ifd0?.GetDescription(ExifDirectoryBase.TagModel),
                Lens: sub?.GetDescription(ExifDirectoryBase.TagLensModel),
                Iso: iso,
                Aperture: aperture,
                Shutter: sub?.GetDescription(ExifDirectoryBase.TagExposureTime),
                FocalLength: focalLength);
        }
        catch (Exception)
        {
            return AssetMetadata.Empty;
        }
    }

    private static int? TryGetDimension(MetadataExtractor.Directory? directory, int tag)
        => directory?.TryGetInt32(tag, out var value) == true && value > 0 ? value : null;

    private static bool IsQuarterTurn(int? orientation)
        => orientation is 5 or 6 or 7 or 8;
}
