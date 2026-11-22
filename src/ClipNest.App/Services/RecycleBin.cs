using System.Runtime.InteropServices;

namespace ClipNest.App.Services;

/// <summary>
/// 按资源管理器的删除语义把文件移入回收站。整批路径一次交给系统，
/// 没有回收站的位置（例如网络共享）会失败，由调用方按剩余文件报告。
/// </summary>
public sealed class RecycleBin : IFileRemover
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public IReadOnlyList<string> Remove(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0) return Array.Empty<string>();

        // 路径列表以空字符分隔，并以两个空字符收尾，第二个空字符是列表终止符。
        var from = string.Join('\0', existing) + "\0\0";
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = from,
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi)
        };

        try
        {
            SHFileOperation(ref operation);
        }
        catch (DllNotFoundException)
        {
            return Array.Empty<string>();
        }

        return existing.Where(path => !File.Exists(path)).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;

        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
