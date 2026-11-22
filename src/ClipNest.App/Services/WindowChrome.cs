using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClipNest.App.Theme;

namespace ClipNest.App.Services;

/// <summary>
/// 自绘标题栏的窗口辅助，负责深色系统窗框、最大化状态切换，
/// 以及扩展客户区最大化后的边框补偿。主窗口与查看器共用。
/// </summary>
public static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmcpRound = 2;
    private const int MonitorDefaultToNearest = 2;

    /// <summary>
    /// 按当前主题设置窗框。深色标志、标题栏与边框颜色都取自窗口底色；主窗与查看器都是自绘标题栏，
    /// 这里只保证窗框外沿不出现亮边或暗边。主题切换后需要重新调用一次。
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero) return;

        var dark = ThemeColors.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle.Value, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // 圆角窗口，外形与 macOS 一致，自绘内容会随之被裁切。
        var corner = DwmcpRound;
        _ = DwmSetWindowAttribute(handle.Value, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        // 标题栏与外沿都取窗口底色，最大化时不会在屏幕边缘留一条异色。
        var caption = ToColorRef(ThemeColors.SurfaceWindow);
        _ = DwmSetWindowAttribute(handle.Value, DwmwaCaptionColor, ref caption, sizeof(int));

        var text = ToColorRef(ThemeColors.TextPrimary);
        _ = DwmSetWindowAttribute(handle.Value, DwmwaTextColor, ref text, sizeof(int));

        var border = caption;
        _ = DwmSetWindowAttribute(handle.Value, DwmwaBorderColor, ref border, sizeof(int));
    }

    /// <summary>COLORREF 是 0x00BBGGRR，与 #RRGGBB 的字节序相反。</summary>
    private static int ToColorRef(Color color)
        => color.R | (color.G << 8) | (color.B << 16);

    public static void ToggleMaximize(Window window)
        => window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// 标题栏拖动。最大化时先还原并把窗口摆到光标下，避免系统还原到屏幕外 8px；
    /// 拖动过程中窗口跟随光标。
    /// </summary>
    public static void BeginWindowDrag(Window window, PointerPressedEventArgs e)
    {
        if (window.WindowState != WindowState.Maximized)
        {
            window.BeginMoveDrag(e);
            return;
        }

        var point = e.GetPosition(window);
        var screen = window.PointToScreen(point);
        var scale = window.RenderScaling;
        if (scale <= 0) scale = 1;

        window.WindowState = WindowState.Normal;

        var x = screen.X - (int)Math.Round(point.X * scale);
        var y = screen.Y - (int)Math.Round(point.Y * scale);
        var display = window.Screens.ScreenFromPoint(screen);
        var workLeft = display?.WorkingArea.X ?? 0;
        var workTop = display?.WorkingArea.Y ?? 0;
        window.Position = new PixelPoint(Math.Max(x, workLeft), Math.Max(y, workTop));

        window.BeginMoveDrag(e);
    }

    /// <summary>
    /// 从最大化直接拖动标题栏时，系统会把还原后的窗口留在屏幕外若干像素。
    /// 这里把左上角拉回工作区，避免内容被裁掉。
    /// </summary>
    public static void EnsureVisible(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window);
        if (screen is null) return;

        var scale = window.RenderScaling;
        if (scale <= 0) scale = 1;

        var work = screen.WorkingArea;
        var width = (int)Math.Round(window.Bounds.Width * scale);
        var height = (int)Math.Round(window.Bounds.Height * scale);

        var x = Math.Max(window.Position.X, work.X);
        var y = Math.Max(window.Position.Y, work.Y);

        // 至少保留标题栏在屏幕内，避免窗口完全拖出可视区域。
        if (width >= work.Width) x = work.X;
        if (height >= work.Height) y = work.Y;

        if (x != window.Position.X || y != window.Position.Y)
            window.Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// 扩展客户区后，Windows 最大化会把窗口向外扩出 8px 的不可见缩放边框，导致内容贴到屏幕外沿。
    /// 这里按工作区算出需要补回的内边距（DIP）。
    /// </summary>
    public static Thickness GetMaximizedInset(Window window)
    {
        if (window.WindowState != WindowState.Maximized) return default;
        if (!OperatingSystem.IsWindows()) return default;

        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle.Value == IntPtr.Zero) return default;
        if (!GetWindowRect(handle.Value, out var rect)) return default;

        var monitor = MonitorFromWindow(handle.Value, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return default;

        var scale = window.RenderScaling;
        if (scale <= 0) scale = 1;

        return new Thickness(
            Math.Max(0, info.Work.Left - rect.Left) / scale,
            Math.Max(0, info.Work.Top - rect.Top) / scale,
            Math.Max(0, rect.Right - info.Work.Right) / scale,
            Math.Max(0, rect.Bottom - info.Work.Bottom) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
