using System.Runtime.InteropServices;

namespace LauncherWinUI;

/// <summary>
/// The tiny sliver of Win32 a WinUI 3 popup still needs: cursor position,
/// work area and the DWM rounded-corner preference. Everything else (backdrop,
/// fonts, rounded card rendering) is native to the framework.
/// </summary>
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point lpPoint);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    /// <summary>Work area of the monitor containing <paramref name="pt"/> (the
    /// popup must clamp against the cursor's monitor, like the WinForms
    /// original's Screen.FromPoint(cursor) — clamping against the primary
    /// monitor pushed the window to the primary's corner when the cursor was
    /// on a different display). Physical pixels, like GetCursorPos.
    /// Falls back to the primary monitor's full bounds.</summary>
    public static Rect GetWorkArea(Point pt)
    {
        IntPtr hMonitor = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info))
            return info.rcWork;

        // Fallback: primary monitor full screen.
        int x = GetSystemMetrics(0);
        int y = GetSystemMetrics(1);
        return new Rect { Left = x, Top = y, Right = x + GetSystemMetrics(78), Bottom = y + GetSystemMetrics(79) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct UIntPtrWrapper
    {
        public uint Value;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint attributeValue, uint attributeSize);

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        uint corner = 2;
        DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(uint));
    }

    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOP = new(-1);
    private const uint ASFW_ANY = 0xFFFFFFFF;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hwnd, int index, int value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out Rect lpRect);

    /// <summary>Move without touching size/z-order (SWP_NOZORDER|SWP_NOSIZE).
    /// Physical pixels, same coordinate space as GetCursorPos. This is the
    /// Win32 primitive WinForms' Location setter uses; AppWindow.Move did not
    /// stick around the WinUI 3 unpackaged Activate/Loaded sequence.</summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOSIZE);
    }

    /// <summary>Make the popup truly borderless (WS_POPUP: no frame, no 1px
    /// DWM border line at the top).</summary>
    public static void MakeBorderless(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style = (style & ~WS_OVERLAPPEDWINDOW) | WS_POPUP;
        SetWindowLong(hwnd, GWL_STYLE, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    /// <summary>Move and place above every other window (HWND_TOP, no
    /// SWP_NOZORDER) and activate it. Unlike MoveWindow, this is what
    /// re-showing a hidden window needs: AppWindow.Show() alone leaves the
    /// window behind the current foreground window.</summary>
    public static void BringToFrontAndMove(IntPtr hwnd, int x, int y)
    {
        SetWindowPos(hwnd, HWND_TOP, x, y, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Give the window real input focus. The show can be requested
    /// by another process (quicklauncher) while we are in the background, in
    /// which case the normal SetForegroundWindow restriction would keep the
    /// window unfocused and hidden-behind; AllowSetForegroundWindow(ASFW_ANY)
    /// lifts that restriction for our own call.</summary>
    public static void BringToForeground(IntPtr hwnd)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        SetForegroundWindow(hwnd);
    }
}
