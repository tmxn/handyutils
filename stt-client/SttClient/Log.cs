namespace SttClient;

/// <summary>
/// Append-only log written to <c>stt-client.log</c> in the configured output
/// directory. Records what was recorded where, request times, and server
/// responses. Failures to write are swallowed — logging must never kill a job.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>Points the log at <c>&lt;dir&gt;/stt-client.log</c>.</summary>
    public static void Init(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        lock (Gate)
        {
            try { _path = Path.Combine(dir, "stt-client.log"); }
            catch { _path = null; }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_path == null) return;
            try { File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
