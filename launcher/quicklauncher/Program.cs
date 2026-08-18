using System.Diagnostics;

namespace QuickLauncher;

/// <summary>
/// Tiny AOT "open the launcher" stub. If launcher_winui is already running
/// (its single-instance mutex exists), it just sets the running instance's
/// show-event so the existing window surfaces, and exits. Otherwise it
/// launches launcher_winui.exe from the folder this stub lives in.
/// </summary>
internal static class Program
{
    // Must match LauncherWinUI.App — these two names are the entire IPC.
    private const string MutexName = @"Local\launcher_winui_single_instance";
    private const string ShowEventName = @"Local\launcher_winui_show";

    private static void Main()
    {
        // Probe for a running primary. The probe mutex is disposed BEFORE we
        // start a new main instance, so the fresh launcher_winui.exe can
        // acquire its own mutex without tripping over us.
        bool running;
        using (var probe = new Mutex(false, MutexName, out bool createdNew))
        {
            running = !createdNew;
            if (running && !SignalShow())
                running = false; // primary never answered — fall through to spawn
        }

        if (running)
            return;

        string mainExe = Path.Combine(AppContext.BaseDirectory, "launcher_winui.exe");
        if (!File.Exists(mainExe))
        {
            Log($"launcher_winui.exe not found next to quicklauncher: {mainExe}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(mainExe)
            {
                UseShellExecute = true, // fire and forget
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception ex)
        {
            Log($"failed to start {mainExe}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ask the running primary to show its window. The primary creates the
    /// event in its App constructor (right after the mutex), so a few retries
    /// cover the startup window.
    /// </summary>
    private static bool SignalShow()
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }

    private static void Log(string message)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".launcher", "quicklauncher.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Nothing else to do — WinExe has no console.
        }
    }
}
