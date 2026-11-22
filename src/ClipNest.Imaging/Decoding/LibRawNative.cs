using System.Runtime.InteropServices;

namespace ClipNest.Imaging.Decoding;

internal static class LibRawNative
{
    private const string Library = "libraw";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libraw_init(uint flags);

    // 走宽字符入口，路径含中文等非 ANSI 字符时也能打开。
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int libraw_open_wfile(IntPtr data, string fileName);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libraw_unpack_thumb(IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr data, out int error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libraw_dcraw_clear_mem(IntPtr image);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libraw_close(IntPtr data);
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibRawImageHeader
{
    public int Type;
    public ushort Height;
    public ushort Width;
    public ushort Colors;
    public ushort Bits;
    public uint DataSize;
}
