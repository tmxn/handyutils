using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LauncherWinUI;

public partial class App : Application
{
    // Names must match quicklauncher/Program.cs — they are the whole IPC.
    public const string MutexName = @"Local\launcher_winui_single_instance";
    public const string ShowEventName = @"Local\launcher_winui_show";

    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".launcher", "launcher_winui.log");

    private Mutex? _instanceMutex; // must outlive the whole process
    private EventWaitHandle? _showEvent;
    private MainWindow? _mainWindow;
    private TrayIcon? _tray;

    public App()
    {
        // Capture startup failures to a log file (the process dies with a
        // stowed exception otherwise, with nothing diagnostic on stderr).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"AppDomain unhandled: {e.ExceptionObject}");
        UnhandledException += (_, e) =>
        {
            Log($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };

        // Single instance: the launcher is now a tray resident, so launching
        // it again (double-click, quicklauncher) must not start a second
        // process — it signals the running one to surface its window.
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log("Another instance is running — signalling it to show, exiting this copy");
            SignalShow();
            Environment.Exit(0);
        }

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Log($"InitializeComponent failed: {ex}");
            throw;
        }

        // Other copies / quicklauncher signal "show the launcher" by setting
        // this event. We are the primary (we own the mutex), so create it.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _mainWindow = new MainWindow();
            _mainWindow.Activate(); // WinUI 3 has no Window.Show(); Activate() is the display call
            Log("OnLaunched OK");

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Powershell.ico");
            _tray = new TrayIcon(iconPath, "launcher_winui");
            _tray.LeftClick += ShowLauncher;
            _tray.MenuCommand += id =>
            {
                switch (id)
                {
                    case TrayIcon.MenuShow:
                        ShowLauncher();
                        break;
                    case TrayIcon.MenuReload:
                        _mainWindow?.ReloadConfig();
                        break;
                    case TrayIcon.MenuExit:
                        Log("exit requested from tray menu");
                        Exit();
                        break;
                }
            };

            // Watch for show requests. WaitOne on a background thread so we
            // never block the UI thread; deliver on the UI dispatcher.
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var showEvent = _showEvent!;
            Task.Run(() =>
            {
                while (true)
                {
                    if (showEvent.WaitOne(250))
                        dispatcher.TryEnqueue(() => ShowLauncher());
                }
            });
        }
        catch (Exception ex)
        {
            Log($"OnLaunched failed: {ex}");
        }
    }

    // (No OnExited hook: Windows reclaims the tray icon when the process
    // dies, and this SDK's Application exposes no override to intercept it.)

    /// <summary>Surface the launcher window at the cursor position.</summary>
    private void ShowLauncher()
    {
        _mainWindow?.ShowAtCursor();
    }

    /// <summary>
    /// Set the primary instance's show event. Retries briefly in case the
    /// primary is between creating its mutex and its event (startup window).
    /// </summary>
    private static void SignalShow()
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(30);
            }
        }
        Log($"Could not open {ShowEventName} — running instance unavailable");
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging itself take the app down.
        }
    }
}
