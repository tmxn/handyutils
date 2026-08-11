using System;
using System.Runtime.InteropServices;

namespace Launcher;

public static partial class GlassInterop
{
    public enum DWMWINDOWATTRIBUTE : uint
    {
        DWMWA_NCRENDERING_POLICY = 2,
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        DWMWA_BORDER_COLOR = 34,
        DWMWA_WINDOW_CORNER_PREFERENCE = 33,
        DWMWA_SYSTEMBACKDROP_TYPE = 38
    }

    public enum DWMNCRENDERINGPOLICY : uint
    {
        DWMNCRP_USEWINDOWSTYLE = 0,
        DWMNCRP_DISABLED = 1,
        DWMNCRP_ENABLED = 2
    }

    public enum DWM_WINDOW_CORNER_PREFERENCE : uint
    {
        DWMWCP_DEFAULT = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND = 2,
        DWMWCP_ROUNDSMALL = 3
    }

    public enum DWMSBT : uint
    {
        DWMSBT_DISABLE = 1,
        DWMSBT_MAINWINDOW = 2,      // Mica
        DWMSBT_TRANSIENTWINDOW = 3, // Desktop Acrylic
        DWMSBT_TABBEDWINDOW = 4     // Acrylic Tabbed
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attribute, ref uint pvAttribute, uint cbAttribute);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    public static void EnableAcrylic(IntPtr hwnd)
    {
        // 1. Force dark mode window frame
        uint darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(uint));

        // 2. Extend sheet of glass across whole client rect
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // 3. Enable Desktop Acrylic backdrop
        uint backdropType = (uint)DWMSBT.DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(uint));
    }

    public static void EnableNativeWindowEffects(IntPtr hwnd, bool darkMode)
    {
        uint cornerPreference = (uint)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(uint));

        uint ncRendering = (uint)DWMNCRENDERINGPOLICY.DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_NCRENDERING_POLICY, ref ncRendering, sizeof(uint));

        uint dark = darkMode ? 1u : 0u;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(uint));
    }

    /// <summary>
    /// Applies Windows 11 Mica to the given window. Returns true when the backdrop
    /// attribute was applied successfully (Windows 11 22H2+), false on older systems.
    /// </summary>
    public static bool EnableMica(IntPtr hwnd, bool darkMode)
    {
        // 1. Native Windows 11 rounded corners (even on borderless windows).
        uint cornerPreference = (uint)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(uint));

        // 2. Dark (or light) immersive frame for native elements.
        uint dark = darkMode ? 1u : 0u;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(uint));

        // 3. Subtle hairline to separate the window edge from the wallpaper.
        uint border = darkMode ? 0x00282828u : 0x00E7E7E7u; // COLORREF (0x00BBGGRR)
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, ref border, sizeof(uint));

        // 4. Extend the DWM frame across the whole client area (sheet of glass).
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // 5. Enable the Mica backdrop (DWMSBT_MAINWINDOW).
        uint backdropType = (uint)DWMSBT.DWMSBT_MAINWINDOW;
        return DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(uint)) == 0;
    }
}
