using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WorkTracker.Services;

/// <summary>
/// App theming: 'auto' follows the OS light/dark preference (Windows registry
/// AppsUseLightTheme), 'light'/'dark' override it. Swaps the theme resource
/// dictionary (Themes/Light.xaml or Dark.xaml) and raises Changed so the UI
/// re-renders. Auto mode keeps watching for live OS theme switches.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static string _mode = "auto";
    private static bool _watching;
    private static string _lastApplied = "";

    /// <summary>"light" or "dark" — the currently applied theme.</summary>
    public static string Current { get; private set; } = "light";

    /// <summary>Raised after a theme is (re)applied. UI subscribes to rebuild chrome.</summary>
    public static event Action? Changed;

    /// <summary>Call once at startup with the configured mode.</summary>
    public static void Initialize(string mode)
    {
        _mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        Apply();

        if (_mode == "auto" && !_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    public static void SetMode(string mode)
    {
        _mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        Apply();
        if (_mode == "auto" && !_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    /// <summary>
    /// Looks up a brush by resource key from the currently applied theme.
    /// Returns a neutral brush (and logs) rather than throwing if the key is missing.
    /// </summary>
    public static Brush Brush(string key)
    {
        try
        {
            if (System.Windows.Application.Current?.Resources[key] is Brush b) return b;
        }
        catch { }
        AppLog.Warn($"missing theme brush '{key}' — using neutral");
        return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Re-evaluate on any user-preference change; cheap, and catches live theme toggles.
        if (_mode == "auto")
            Apply();
    }

    private static void Apply()
    {
        var dark = _mode == "dark" || (_mode == "auto" && IsSystemDark());
        Current = dark ? "dark" : "light";
        if (Current == _lastApplied) return; // no-op: already applied

        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            var list = app.Resources.MergedDictionaries;
            if (list.Count == 0) list.Add(new ResourceDictionary());
            var uri = new Uri($"pack://application:,,,/Themes/{Current}.xaml", UriKind.Absolute);
            list[0].Source = uri;
            _lastApplied = Current;
            AppLog.Info($"theme applied: {Current}");
        }
        catch (Exception ex)
        {
            AppLog.Error("failed to apply theme", ex);
            return;
        }

        Changed?.Invoke();
    }

    public static bool IsSystemDark()
    {
        try
        {
            var v = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }
}
