using System.IO;

namespace WorkTracker.Services;

/// <summary>
/// Lightweight append-only file log for diagnosing real-repo runs:
/// %USERPROFILE%\WorkTrackerData\log\worktracker.log
/// Thread-safe; self-trimming (keeps the tail once the file exceeds ~1 MB).
/// Never throws — logging must not break the app.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const int MaxBytes = 1_000_000;

    private static string LogPath => Path.Combine(ConfigStore.DataDir, "log", "worktracker.log");

    public static void Info(string message) => Write("info ", message);
    public static void Warn(string message) => Write("warn ", message);
    public static void Error(string message) => Write("error", message);
    public static void Error(string message, Exception? ex) =>
        Error(ex != null ? $"{message} :: {ex}" : message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");

                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                }
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
