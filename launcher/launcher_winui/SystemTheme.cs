using Microsoft.Win32;

namespace LauncherWinUI;

/// <summary>
/// Registry probes for the OS theme — the same keys the old WinForms
/// ThemeHelper read, but without System.Drawing dependency.
/// </summary>
internal static class SystemTheme
{
    /// <summary>True when the user has opted into dark mode for apps.</summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch
        {
            // Fall through to light.
        }

        return false;
    }

    /// <summary>True on Windows 11 build 22000+ (required for the Mica backdrop).</summary>
    public static bool IsWindows11()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuild") is string build &&
                int.TryParse(build, out int number) &&
                number >= 22000)
                return true;
        }
        catch
        {
            // Ignore.
        }

        return false;
    }
}
