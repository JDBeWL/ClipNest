using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClipNest.App.Services;

/// <summary>
/// 在资源管理器中定位文件。目标文件夹已打开时在原窗口里选中并置前，标签页也算已打开；
/// 只有未打开的文件夹才新开窗口。
/// </summary>
public static class ExplorerReveal
{
    // 选中、取消其余选中、滚入视野、聚焦。
    private const int SelectFlags = 0x1 | 0x4 | 0x8 | 0x10;
    private const int SwRestore = 9;

    public static bool Reveal(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            if (TrySelectInOpenWindow(filePath)) return true;
        }
        catch
        {
            // COM 不可用时退回新开窗口。
        }

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

    // Shell.Application 只能通过 dynamic 调用。TrimMode=partial 下 ClipNest.App
    // 不会被裁剪，dynamic 依赖的成员与构造函数都完整保留，这里的裁剪警告是误报。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "本程序集不做裁剪，dynamic 调用的成员保留。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "本程序集不做裁剪，Shell.Application 类型由系统注册，不参与裁剪。")]
    private static bool TrySelectInOpenWindow(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)) return false;

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;

        foreach (dynamic window in shell.Windows())
        {
            string? url;
            try
            {
                url = window.LocationURL as string;
            }
            catch
            {
                continue;
            }
            if (!MatchesFolder(url, folder)) continue;

            window.Document.SelectItem(filePath, SelectFlags);
            var handle = new IntPtr(Convert.ToInt64(window.HWND));
            if (IsIconic(handle)) ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            return true;
        }

        return false;
    }

    internal static bool MatchesFolder(string? locationUrl, string folder)
    {
        if (string.IsNullOrEmpty(locationUrl) ||
            !locationUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(
                new Uri(locationUrl).LocalPath.TrimEnd(Path.DirectorySeparatorChar),
                folder.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}