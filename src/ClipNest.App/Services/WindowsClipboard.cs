using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using ClipNest.Imaging.Decoding;
using SkiaSharp;

namespace ClipNest.App.Services;

/// <summary>
/// 写入 Windows 原生剪贴板格式。Avalonia 只提供文本与自定义格式，
/// 文件列表需要 CF_HDROP，图片需要 CF_DIB，其他程序才能直接粘贴。
/// </summary>
public static class WindowsClipboard
{
    private const uint CfDib = 8;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;

    // 接收程序通常不会使用长边超过 8K 的图片，限制长边可以避免超大图解码后占满内存。
    private const int MaxImageEdge = 8192;

    /// <summary>把文件放到剪贴板，资源管理器与聊天软件会按文件接收。</summary>
    public static bool CopyFiles(IReadOnlyList<string> paths)
    {
        var files = paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return false;

        // DROPFILES 头之后是 UTF-16 路径列表，以双空字符结束。
        var headerSize = Marshal.SizeOf<DropFiles>();
        var characters = files.Sum(path => path.Length + 1) + 1;
        var bytes = headerSize + characters * 2;
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
        if (handle == IntPtr.Zero) return false;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        try
        {
            var header = new DropFiles { pFiles = (uint)headerSize, fWide = 1 };
            Marshal.StructureToPtr(header, pointer, false);

            var cursor = IntPtr.Add(pointer, headerSize);
            foreach (var file in files)
            {
                var text = file + "\0";
                Marshal.Copy(text.ToCharArray(), 0, cursor, text.Length);
                cursor = IntPtr.Add(cursor, text.Length * 2);
            }

            Marshal.WriteInt16(cursor, 0);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return Write(handle, CfHDrop);
    }

    /// <summary>
    /// 把图片放到剪贴板。优先解码原文件；RAW、HEIC 等格式解不出来时，
    /// 使用查看器已经显示的位图，保证仍能粘贴。
    /// </summary>
    public static async Task<bool> CopyImageAsync(string path, Bitmap? fallback)
    {
        var handle = await Task.Run(() => BuildFromFileAsync(path));
        if (handle == IntPtr.Zero && fallback is not null)
            handle = BuildFromBitmap(fallback);
        if (handle == IntPtr.Zero) return false;

        return Write(handle, CfDib);
    }

    /// <summary>按 EXIF 方向解码原文件，竖拍图片进入剪贴板后按正确方向显示。</summary>
    private static async Task<IntPtr> BuildFromFileAsync(string path)
    {
        try
        {
            var decoder = new SkiaImageDecoder();
            using var decoded = await decoder
                .DecodeAsync(path, MaxImageEdge)
                .ConfigureAwait(false);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return IntPtr.Zero;

            using var converted = decoded.ColorType == SKColorType.Bgra8888
                ? null
                : decoded.Copy(SKColorType.Bgra8888);
            var source = converted ?? decoded;

            var length = source.RowBytes * source.Height;
            var pixels = new byte[length];
            Marshal.Copy(source.GetPixels(), pixels, 0, length);

            return BuildDib(
                source.Width,
                source.Height,
                source.RowBytes,
                pixels,
                source.AlphaType == SKAlphaType.Premul);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr BuildFromBitmap(Bitmap bitmap)
    {
        try
        {
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return IntPtr.Zero;

            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(
                    new PixelRect(0, 0, size.Width, size.Height),
                    pinned.AddrOfPinnedObject(),
                    pixels.Length,
                    stride);
            }
            finally
            {
                pinned.Free();
            }

            // Avalonia 的位图按预乘 alpha 存放，写进 DIB 前要还原为直通 alpha。
            return BuildDib(size.Width, size.Height, stride, pixels, premultiplied: true);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 生成 BITMAPINFOHEADER 加自下而上的像素行。DIB 的落址在系统堆上，
    /// 成功后所有权交给剪贴板，调用方不得再释放。
    /// </summary>
    private static IntPtr BuildDib(
        int width, int height, int rowBytes, byte[] pixels, bool premultiplied)
    {
        var stride = width * 4;
        var size = 40 + stride * height;
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)size);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            WriteHeader(pointer, width, height, stride * height);

            var destination = IntPtr.Add(pointer, 40);
            var row = new byte[stride];
            for (var y = height - 1; y >= 0; y--)
            {
                var copy = Math.Min(rowBytes, stride);
                Buffer.BlockCopy(pixels, y * rowBytes, row, 0, copy);
                if (copy < stride) Array.Clear(row, copy, stride - copy);
                if (premultiplied) Unpremultiply(row);

                Marshal.Copy(row, 0, destination, stride);
                destination = IntPtr.Add(destination, stride);
            }
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static void WriteHeader(IntPtr pointer, int width, int height, int imageSize)
    {
        var header = new byte[40];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), 40);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), width);
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), height);
        BitConverter.TryWriteBytes(header.AsSpan(12, 2), (short)1);
        BitConverter.TryWriteBytes(header.AsSpan(14, 2), (short)32);
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), 0);
        BitConverter.TryWriteBytes(header.AsSpan(20, 4), imageSize);
        Marshal.Copy(header, 0, pointer, header.Length);
    }

    private static void Unpremultiply(byte[] row)
    {
        for (var index = 0; index < row.Length; index += 4)
        {
            var alpha = row[index + 3];
            if (alpha is 0 or 255) continue;

            row[index] = (byte)Math.Min(255, row[index] * 255 / alpha);
            row[index + 1] = (byte)Math.Min(255, row[index + 1] * 255 / alpha);
            row[index + 2] = (byte)Math.Min(255, row[index + 2] * 255 / alpha);
        }
    }

    private static bool Write(IntPtr handle, uint format)
    {
        if (!TryOpenClipboard())
        {
            GlobalFree(handle);
            return false;
        }

        var owned = false;
        try
        {
            EmptyClipboard();
            owned = SetClipboardDataNative(format, handle) != IntPtr.Zero;
            return owned;
        }
        finally
        {
            CloseClipboard();
            if (!owned) GlobalFree(handle);
        }
    }

    /// <summary>剪贴板可能被其他进程短暂占用，重试几次再放弃。</summary>
    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint pFiles;
        public int x;
        public int y;
        public int fNC;
        public int fWide;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", EntryPoint = "SetClipboardData", SetLastError = true)]
    private static extern IntPtr SetClipboardDataNative(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
