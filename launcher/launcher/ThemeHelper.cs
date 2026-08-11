using System.Drawing;
using Microsoft.Win32;

namespace Launcher;

public readonly record struct LauncherPalette(
    Color FormBackground,
    Color LeftPanelBackground,
    Color RightPanelBackground,
    Color FormBorder,
    Color ButtonBackground,
    Color ButtonHoverBackground,
    Color ButtonSelectedBackground,
    Color ButtonBorder,
    Color Text);

public static class ThemeHelper
{
    /// <summary>Set once Mica is confirmed to be active on the current window/OS.</summary>
    public static bool MicaEnabled { get; set; }

    private static Color GetSystemAccentColor()
    {
        try
        {
            // Windows stores the accent as 0x00BBGGRR in the registry.
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key != null)
            {
                if (key.GetValue("AccentColor") is int acc)
                {
                    int r = acc & 0xFF;
                    int g = (acc >> 8) & 0xFF;
                    int b = (acc >> 16) & 0xFF;
                    if (r != 0 || g != 0 || b != 0)
                        return Color.FromArgb(r, g, b);
                }
            }
            // Fallback to DWM ColorizationColor
            using var dwmKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwmKey != null && dwmKey.GetValue("ColorizationColor") is int col)
            {
                int r = col & 0xFF;
                int g = (col >> 8) & 0xFF;
                int b = (col >> 16) & 0xFF;
                return Color.FromArgb(r, g, b);
            }
        }
        catch { }
        // Default Fluent blue
        return Color.FromArgb(0, 120, 215);
    }

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
            // Ignore and fall back to light mode.
        }

        return false;
    }

    public static LauncherPalette GetPalette()
    {
        bool dark = IsDarkMode();

        if (MicaEnabled)
        {
            // Translucent Fluent "cards" drawn over the DWM Mica backdrop.
            // Selected state uses the same white-ish hover colour as the hover state.
            return dark
                ? new LauncherPalette(
                    FormBackground: Color.Black,
                    LeftPanelBackground: Color.Transparent,
                    RightPanelBackground: Color.Transparent,
                    FormBorder: Color.FromArgb(32, 255, 255, 255),
                    ButtonBackground: Color.FromArgb(48, 255, 255, 255),
                    ButtonHoverBackground: Color.FromArgb(86, 255, 255, 255),
                    ButtonSelectedBackground: Color.FromArgb(86, 255, 255, 255),
                    ButtonBorder: Color.FromArgb(30, 255, 255, 255),
                    Text: Color.FromArgb(243, 243, 243))
                : new LauncherPalette(
                    FormBackground: Color.Black,
                    LeftPanelBackground: Color.Transparent,
                    RightPanelBackground: Color.Transparent,
                    FormBorder: Color.FromArgb(36, 0, 0, 0),
                    ButtonBackground: Color.FromArgb(110, 255, 255, 255),
                    ButtonHoverBackground: Color.FromArgb(140, 255, 255, 255),
                    ButtonSelectedBackground: Color.FromArgb(140, 255, 255, 255),
                    ButtonBorder: Color.FromArgb(40, 0, 0, 0),
                    Text: Color.FromArgb(28, 28, 28));
        }

        if (dark)
        {
            // Fluent-inspired dark palette (fallback when Mica is unavailable)
            return new LauncherPalette(
                FormBackground: Color.FromArgb(32, 32, 32),
                LeftPanelBackground: Color.FromArgb(43, 43, 43),
                RightPanelBackground: Color.FromArgb(38, 38, 38),
                FormBorder: Color.FromArgb(84, 84, 84),
                ButtonBackground: Color.FromArgb(48, 48, 48),
                ButtonHoverBackground: Color.FromArgb(61, 61, 61),
                ButtonSelectedBackground: Color.FromArgb(74, 74, 74),
                ButtonBorder: Color.FromArgb(98, 98, 98),
                Text: Color.FromArgb(243, 243, 243));
        }

        // Fluent-inspired light palette (fallback when Mica is unavailable)
        return new LauncherPalette(
            FormBackground: Color.FromArgb(243, 243, 243),
            LeftPanelBackground: Color.FromArgb(251, 251, 251),
            RightPanelBackground: Color.FromArgb(247, 247, 247),
            FormBorder: Color.FromArgb(206, 206, 206),
            ButtonBackground: Color.FromArgb(255, 255, 255),
            ButtonHoverBackground: Color.FromArgb(243, 247, 253),
            ButtonSelectedBackground: Color.FromArgb(233, 241, 253),
            ButtonBorder: Color.FromArgb(208, 215, 226),
            Text: Color.FromArgb(28, 28, 28));
    }
}
