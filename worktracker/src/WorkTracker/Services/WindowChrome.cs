using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkTracker.Services;

/// <summary>
/// Windows 11 DWM window chrome: dark title bar + Mica backdrop — the same
/// backdrop Windows Notepad/Paint use.
///
/// Applied per top-level window:
///   - DWMWA_USE_IMMERSIVE_DARK_MODE (attr 20, Win10 20H2+): dark caption and
///     light caption glyphs so the title bar matches the app's dark theme
///     instead of the OS default (bright) caption.
///   - DWMWA_SYSTEMBACKDROP_TYPE (attr 38, Win11 22H2+): Mica backdrop behind
///     the client area. The window's Background must then be transparent for
///     the Mica to show through. The Mica tint always follows the *OS* theme,
///     not the app theme — in 'auto' mode the two agree.
///
/// Everything fails gracefully: unsupported attributes return non-zero and the
/// window keeps the default solid chrome (Windows 10, pre-22H2, ...).
/// </summary>
internal static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWM_SYSTEMBACKDROP_TYPE_MICA = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies Win11 chrome to the window. Call on SourceInitialized, and again
    /// after every theme change. <paramref name="dark"/> is the app's currently
    /// applied theme (decides the caption color).
    /// Returns true when the Mica backdrop was applied — in that case the
    /// window's Background should be transparent so the Mica shows through;
    /// keep the solid theme background when false.
    /// </summary>
    public static bool Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var darkCaption = Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        var mica = Set(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWM_SYSTEMBACKDROP_TYPE_MICA);
        AppLog.Info($"win11 chrome: dark caption {(darkCaption ? "ok" : "n/a")}, " +
                    $"mica {(mica ? "ok" : "unavailable — solid background")}");
        return mica;
    }

    private static bool Set(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, Marshal.SizeOf<int>()) == 0;
        }
        catch
        {
            return false;
        }
    }
}
