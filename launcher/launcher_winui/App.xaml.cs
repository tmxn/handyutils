using System.Text;
using Microsoft.UI.Xaml;

namespace LauncherWinUI;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".launcher", "launcher_winui.log");

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

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Log($"InitializeComponent failed: {ex}");
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var window = new MainWindow();
            window.Activate(); // WinUI 3 has no Window.Show(); Activate() is the display call
            Log("OnLaunched OK");
        }
        catch (Exception ex)
        {
            Log($"OnLaunched failed: {ex}");
        }
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
