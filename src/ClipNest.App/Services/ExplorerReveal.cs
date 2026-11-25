using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipNest.App.Services;

/// <summary>
/// 在资源管理器中定位文件。文件夹已打开时由系统复用原窗口，
/// 未打开时按系统设置新开窗口或标签页。
/// </summary>
public static class ExplorerReveal
{
    public static bool Reveal(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        if (TryOpenFolderAndSelect(filePath)) return true;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 不得改用 Shell.Application 自动化。裁剪发布下 dynamic 的 COM 绑定
    // 会在 ITypeInfo 上触发访问冲突并终止进程，此处只依赖 shell32 的导出函数。
    private static bool TryOpenFolderAndSelect(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)) return false;

        var folderPidl = IntPtr.Zero;
        var filePidl = IntPtr.Zero;
        var items = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) < 0 ||
                folderPidl == IntPtr.Zero)
            {
                return false;
            }

            uint count = 0;
            if (SHParseDisplayName(filePath, IntPtr.Zero, out filePidl, 0, out _) >= 0 &&
                filePidl != IntPtr.Zero)
            {
                // apidl 取相对该文件夹的子项 PIDL，绝对 PIDL 的最后一项即此子项。
                items = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(items, ILFindLastID(filePidl));
                count = 1;
            }

            return SHOpenFolderAndSelectItems(folderPidl, count, items, 0) >= 0;
        }
        finally
        {
            if (items != IntPtr.Zero) Marshal.FreeHGlobal(items);
            if (filePidl != IntPtr.Zero) ILFree(filePidl);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
