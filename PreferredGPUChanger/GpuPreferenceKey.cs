using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace HeadlessGpuManager;

/// <summary>
/// Resolves the registry value name that UserGpuPreferences actually honours for a
/// given application.
///
/// Windows keys this preference two different ways:
///   * Unpackaged apps  -> the full executable path.
///   * Packaged (MSIX)  -> the AUMID, "PackageFamilyName!ApplicationId".
///
/// Writing a path for a packaged app produces an entry Windows silently ignores, and
/// because packages install under a version-stamped WindowsApps folder the dead entry
/// is orphaned again on every update. Always route writes and lookups through here.
/// </summary>
public static class GpuPreferenceKey
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    const string WindowsAppsMarker = @"\WindowsApps\";

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint length, StringBuilder id);

    /// <summary>
    /// The key to use for a running process. Prefers the AUMID reported by the OS
    /// (authoritative), falling back to manifest inspection and finally the path.
    /// </summary>
    public static string ForProcess(Process proc, string exePath)
    {
        string? aumid = TryGetAumid(proc.Id);
        return string.IsNullOrEmpty(aumid) ? ForPath(exePath) : aumid;
    }

    /// <summary>
    /// The key to use when only a path is known (no live process). Packaged apps are
    /// detected by their WindowsApps location and resolved via AppxManifest.xml.
    /// </summary>
    public static string ForPath(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || exePath.StartsWith("Unknown")) return exePath;
        return TryGetAumidFromManifest(exePath) ?? exePath;
    }

    /// <summary>True if the key is an AUMID rather than a filesystem path.</summary>
    public static bool IsAumid(string key)
        => key.Contains('!') && !key.Contains(Path.DirectorySeparatorChar);

    static string? TryGetAumid(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            uint length = 0;
            // The first call sizes the buffer. Anything other than "too small" means the
            // process is not packaged (APPMODEL_ERROR_NO_APPLICATION) or is unreadable.
            int hr = GetApplicationUserModelId(handle, ref length, new StringBuilder(0));
            if (hr != ERROR_INSUFFICIENT_BUFFER || length == 0) return null;

            var buffer = new StringBuilder((int)length);
            return GetApplicationUserModelId(handle, ref length, buffer) == 0
                ? buffer.ToString()
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Derives "Family!AppId" from a WindowsApps path by reading the package manifest
    /// at the package root. Used for paths with no running process behind them, e.g.
    /// when auditing or pruning stale entries.
    /// </summary>
    static string? TryGetAumidFromManifest(string exePath)
    {
        int marker = exePath.IndexOf(WindowsAppsMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;

        int start = marker + WindowsAppsMarker.Length;
        int end = exePath.IndexOf(Path.DirectorySeparatorChar, start);
        if (end < 0) return null;

        // Folder layout is Name_Version_Arch__PublisherId; the family drops version and
        // architecture, which is exactly why the path rots and the AUMID does not.
        string[] parts = exePath[start..end].Split('_');
        if (parts.Length < 5) return null;
        string family = $"{parts[0]}_{parts[4]}";

        string manifest = Path.Combine(exePath[..end], "AppxManifest.xml");
        if (!File.Exists(manifest)) return null;

        try
        {
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            string? appId = XDocument.Load(manifest)
                .Descendants(ns + "Application")
                .FirstOrDefault()?.Attribute("Id")?.Value;
            return string.IsNullOrEmpty(appId) ? null : $"{family}!{appId}";
        }
        catch
        {
            return null;
        }
    }
}
