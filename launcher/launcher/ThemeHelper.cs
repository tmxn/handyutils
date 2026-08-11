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
            return dark
                ? new LauncherPalette(
                    FormBackground: Color.Black,
                    LeftPanelBackground: Color.Transparent,
                    RightPanelBackground: Color.Transparent,
                    FormBorder: Color.FromArgb(32, 255, 255, 255),
                    ButtonBackground: Color.FromArgb(48, 255, 255, 255),
                    ButtonHoverBackground: Color.FromArgb(86, 255, 255, 255),
                    ButtonSelectedBackground: Color.FromArgb(70, 0, 120, 215),
                    ButtonBorder: Color.FromArgb(30, 255, 255, 255),
                    Text: Color.FromArgb(243, 243, 243))
                : new LauncherPalette(
                    FormBackground: Color.Black,
                    LeftPanelBackground: Color.Transparent,
                    RightPanelBackground: Color.Transparent,
                    FormBorder: Color.FromArgb(36, 0, 0, 0),
                    ButtonBackground: Color.FromArgb(110, 255, 255, 255),
                    ButtonHoverBackground: Color.FromArgb(140, 255, 255, 255),
                    ButtonSelectedBackground: Color.FromArgb(120, 0, 120, 215),
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
