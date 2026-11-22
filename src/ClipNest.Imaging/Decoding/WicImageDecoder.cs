using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using ClipNest.Core.Indexing;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SkiaSharp;

namespace ClipNest.Imaging.Decoding;

/// <summary>
/// 通过 Windows Imaging Component 的 COM 接口解码 HEIC/HEIF/AVIF，
/// 避免引入 WinRT 投影程序集。
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    private const uint GenericRead = 0x80000000;
    private const int ClsCtxInProcServer = 1;
    private const int WicDecodeMetadataCacheOnDemand = 0;
    private const int WicBitmapDitherTypeNone = 0;
    private const int WicBitmapPaletteTypeCustom = 0;

    private static readonly Guid ClsidWicImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    private static readonly Guid IidWicImagingFactory = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
    private static readonly Guid WicPixelFormat32bppPBgra = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

    public bool CanDecode(string extension) => SupportedFormats.Heic.Contains(extension);

    public Task<ImageInfo?> GetInfoAsync(string path, CancellationToken ct = default)
        => Task.Run(() => ReadInfo(path), ct);

    public Task<SKBitmap?> DecodeAsync(string path, int maxSize, CancellationToken ct = default)
        => Task.Run(() => DecodeCore(path, maxSize, ct), ct);

    private static ImageInfo? ReadInfo(string path)
    {
        try
        {
            return WithFactory(factory =>
            {
                IWICBitmapDecoder? decoder = null;
                IWICBitmapFrameDecode? frame = null;
                try
                {
                    decoder = CreateDecoder(factory, path);
                    decoder.GetFrame(0, out frame);
                    if (frame.GetSize(out var width, out var height) < 0) return null;
                    return width == 0 || height == 0
                        ? null
                        : new ImageInfo((int)width, (int)height, 1);
                }
                finally
                {
                    Release(frame);
                    Release(decoder);
                }
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SKBitmap? DecodeCore(string path, int maxSize, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            return WithFactory(factory =>
            {
                IWICBitmapDecoder? decoder = null;
                IWICBitmapFrameDecode? frame = null;
                IWICFormatConverter? converter = null;
                SKBitmap? bitmap = null;

                try
                {
                    decoder = CreateDecoder(factory, path);
                    decoder.GetFrame(0, out frame);
                    if (frame.GetSize(out var width, out var height) < 0) return null;
                    if (width == 0 || height == 0) return null;

                    var limit = maxSize > 0 ? maxSize : int.MaxValue;
                    var scale = Math.Min(1.0, limit / (double)Math.Max(width, height));
                    var targetWidth = (int)Math.Max(1, Math.Round(width * scale));
                    var targetHeight = (int)Math.Max(1, Math.Round(height * scale));

                    factory.CreateFormatConverter(out converter);
                    var format = WicPixelFormat32bppPBgra;
                    if (converter.Initialize(
                            (IWICBitmapSource)frame, ref format, WicBitmapDitherTypeNone,
                            IntPtr.Zero, 0, WicBitmapPaletteTypeCustom) < 0)
                    {
                        return null;
                    }

                    bitmap = new SKBitmap(new SKImageInfo(
                        (int)width, (int)height,
                        SKColorType.Bgra8888, SKAlphaType.Premul));

                    var stride = checked(width * 4);
                    var bufferSize = checked(stride * height);
                    converter.CopyPixels(IntPtr.Zero, stride, bufferSize, bitmap.GetPixels());

                    ct.ThrowIfCancellationRequested();

                    if (scale < 1.0)
                    {
                        var resized = bitmap.Resize(
                            new SKImageInfo(
                                targetWidth, targetHeight,
                                SKColorType.Bgra8888, SKAlphaType.Premul),
                            SKFilterQuality.High);
                        bitmap.Dispose();
                        bitmap = resized;
                        if (bitmap is null) return null;
                    }

                    var orientation = ReadOrientation(path);
                    if (orientation is >= 2 and <= 8)
                    {
                        using (bitmap)
                        {
                            return SkiaImageDecoder.ApplyOrientation(
                                bitmap, (SKEncodedOrigin)orientation);
                        }
                    }

                    var result = bitmap;
                    bitmap = null;
                    return result;
                }
                finally
                {
                    bitmap?.Dispose();
                    Release(converter);
                    Release(frame);
                    Release(decoder);
                }
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IWICBitmapDecoder CreateDecoder(IWICImagingFactory factory, string path)
    {
        factory.CreateDecoderFromFilename(
            path, IntPtr.Zero, GenericRead, WicDecodeMetadataCacheOnDemand, out var decoder);
        return decoder;
    }

    private static int ReadOrientation(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            return ifd0?.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value) == true
                   && value is >= 1 and <= 8
                ? value
                : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // TrimMode=partial 只裁剪标记为 IsTrimmable 的程序集，本程序集不会被裁剪，
    // ComImport 接口元数据完整保留；裁剪发布下 HEIC 解码已实测正常。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2050",
        Justification = "COM 接口由本程序集静态声明，partial 裁剪不会移除该程序集。")]
    private static T? WithFactory<T>(Func<IWICImagingFactory, T?> action)
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0);
        var initialized = hr >= 0;
        if (hr < 0 && hr != RpcEChangedMode)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            var clsid = ClsidWicImagingFactory;
            var iid = IidWicImagingFactory;
            var create = CoCreateInstance(
                ref clsid, IntPtr.Zero, ClsCtxInProcServer, ref iid, out var factory);
            Marshal.ThrowExceptionForHR(create);

            try
            {
                return action(factory);
            }
            finally
            {
                Release(factory);
            }
        }
        finally
        {
            if (initialized) CoUninitialize();
        }
    }

    private static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance)) return;

        try
        {
            Marshal.ReleaseComObject(instance);
        }
        catch (ArgumentException)
        {
            // 已释放的 COM 包装器不需要再次释放。
        }
    }

    private const int RpcEChangedMode = unchecked((int)0x80010106);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IWICImagingFactory ppv);

    [ComImport]
    [Guid("00000120-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        [PreserveSig]
        int GetSize(out uint puiWidth, out uint puiHeight);

        void GetPixelFormat(out Guid pPixelFormat);

        void GetResolution(out double pDpiX, out double pDpiY);

        void CopyPalette(IntPtr pIPalette);

        void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pvBuffer);
    }

    [ComImport]
    [Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability(IntPtr pIStream, out uint pdwCapability);

        void Initialize(IntPtr pIStream, int cacheOptions);

        void GetContainerFormat(out Guid pguidContainerFormat);

        void GetDecoderInfo(IntPtr ppIDecoderInfo);

        void CopyPalette(IntPtr pIPalette);

        void GetMetadataQueryReader(IntPtr ppIMetadataQueryReader);

        void GetPreview(IntPtr ppIBitmapSource);

        void GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount);

        void GetThumbnail(IntPtr ppIThumbnail);

        void GetFrameCount(out uint pCount);

        void GetFrame(uint index, out IWICBitmapFrameDecode ppIBitmapFrame);
    }

    [ComImport]
    [Guid("3B16811B-6A43-4EC9-A813-3D930C13B940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode
    {
        [PreserveSig]
        int GetSize(out uint puiWidth, out uint puiHeight);

        void GetPixelFormat(out Guid pPixelFormat);

        void GetResolution(out double pDpiX, out double pDpiY);

        void CopyPalette(IntPtr pIPalette);

        void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pvBuffer);

        void GetMetadataQueryReader(IntPtr ppIMetadataQueryReader);

        void GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount);

        void GetThumbnail(IntPtr ppIThumbnail);
    }

    [ComImport]
    [Guid("00000301-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        [PreserveSig]
        int GetSize(out uint puiWidth, out uint puiHeight);

        void GetPixelFormat(out Guid pPixelFormat);

        void GetResolution(out double pDpiX, out double pDpiY);

        void CopyPalette(IntPtr pIPalette);

        void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pvBuffer);

        [PreserveSig]
        int Initialize(
            IWICBitmapSource pISource,
            ref Guid dstFormat,
            int dither,
            IntPtr pIPalette,
            double alphaThresholdPercent,
            int paletteTranslate);

        [PreserveSig]
        int CanConvert(ref Guid srcPixelFormat, ref Guid dstPixelFormat, out int pfCanConvert);
    }

    [ComImport]
    [Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename(
            [MarshalAs(UnmanagedType.LPWStr)] string wzFilename,
            IntPtr pguidVendor,
            uint dwDesiredAccess,
            int metadataOptions,
            out IWICBitmapDecoder ppIDecoder);

        void CreateDecoderFromStream(
            IntPtr pIStream, IntPtr pguidVendor, int metadataOptions,
            out IWICBitmapDecoder ppIDecoder);

        void CreateDecoderFromFileHandle(
            UIntPtr hFile, IntPtr pguidVendor, int metadataOptions,
            out IWICBitmapDecoder ppIDecoder);

        void CreateComponentInfo(ref Guid clsidComponent, IntPtr ppIInfo);

        void CreateDecoder(
            ref Guid guidContainerFormat, IntPtr pguidVendor,
            out IWICBitmapDecoder ppIDecoder);

        void CreateEncoder(
            ref Guid guidContainerFormat, IntPtr pguidVendor, IntPtr ppIEncoder);

        void CreatePalette(IntPtr ppIPalette);

        void CreateFormatConverter(out IWICFormatConverter ppIFormatConverter);
    }
}
