using System.Runtime.InteropServices;
using ClipNest.Core.Indexing;
using SkiaSharp;

namespace ClipNest.Imaging.Decoding;

public sealed class LibRawImageDecoder : IImageDecoder
{
    public bool CanDecode(string extension) => SupportedFormats.Raw.Contains(extension);

    public Task<ImageInfo?> GetInfoAsync(string path, CancellationToken ct = default)
        => Task.Run(() => DecodePreview(path) is { } bitmap
            ? new ImageInfo(bitmap.Width, bitmap.Height, 1)
            : null, ct);

    public Task<SKBitmap?> DecodeAsync(string path, int maxSize, CancellationToken ct = default)
        => Task.Run(() => Resize(DecodePreview(path), maxSize), ct);

    private static SKBitmap? DecodePreview(string path)
    {
        if (!File.Exists(path)) return null;

        var data = IntPtr.Zero;

        try
        {
            data = LibRawNative.libraw_init(0);
            if (data == IntPtr.Zero) return null;

            if (LibRawNative.libraw_open_wfile(data, path) != 0) return null;
            if (LibRawNative.libraw_unpack_thumb(data) != 0) return null;

            var thumb = LibRawNative.libraw_dcraw_make_mem_thumb(data, out _);
            if (thumb == IntPtr.Zero) return null;

            try
            {
                var header = Marshal.PtrToStructure<LibRawImageHeader>(thumb);
                var bytes = new byte[header.DataSize];
                Marshal.Copy(IntPtr.Add(thumb, Marshal.SizeOf<LibRawImageHeader>()), bytes, 0, bytes.Length);

                // type=1 是内嵌 JPEG；V1a 只读取预览，不做全尺寸去马赛克。
                return header.Type == 1 ? SKBitmap.Decode(bytes) : null;
            }
            finally
            {
                LibRawNative.libraw_dcraw_clear_mem(thumb);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (data != IntPtr.Zero)
                LibRawNative.libraw_close(data);
        }
    }

    private static SKBitmap? Resize(SKBitmap? source, int maxSize)
    {
        if (source is null) return null;
        if (source.Width <= maxSize && source.Height <= maxSize) return source;

        var scale = (double)maxSize / Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = source.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
        source.Dispose();
        return resized;
    }
}
