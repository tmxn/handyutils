using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TinyLlama;

static class Program
{
    private const int MaxLogLines = 30;
    private const string LlamafilePath = @"D:\llama\Qwen3.5-0.8B-Q8_0.llamafile.exe";

    private static readonly Queue<string> _logLines = new();
    private static readonly object _logLock = new();
    private static readonly StringBuilder _logBuilder = new();

    private static NotifyIcon? _trayIcon;
    private static System.Windows.Forms.Timer? _tooltipTimer;
    private static Process? _llamaProcess;
    private static CancellationTokenSource? _cts;

    [STAThread]
    static void Main()
    {
        // Keep the app alive
        ApplicationConfiguration.Initialize();

        // Build tray menu
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TinyLlama - Starting...",
            ContextMenuStrip = menu,
            Visible = true,
        };

        // Double-click to show log
        _trayIcon.DoubleClick += (_, _) => ShowLog();

        // Update tooltip every 2 seconds
        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _tooltipTimer.Tick += (_, _) => UpdateTooltip();
        _tooltipTimer.Start();

        // Start llamafile
        _cts = new CancellationTokenSource();
        StartLlama();

        // Safety net: kill child process on any exit
        Application.ApplicationExit += (_, _) => KillLlama();

        // Run the message loop
        Application.Run();
    }

    static void StartLlama()
    {
        var psi = new ProcessStartInfo
        {
            FileName = LlamafilePath,
            Arguments = "--server --host 0.0.0.0 --jinja --ctx-size 1024 --port 8081",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _llamaProcess = new Process { StartInfo = psi };

        _llamaProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                AddLogLine(e.Data);
        };

        _llamaProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                AddLogLine(e.Data);
        };

        _llamaProcess.EnableRaisingEvents = true;
        _llamaProcess.Exited += (_, _) =>
        {
            AddLogLine($"[llamafile exited with code {_llamaProcess.ExitCode}]");
            _trayIcon?.ShowBalloonTip(3000, "TinyLlama", "llamafile process exited!", ToolTipIcon.Warning);
        };

        _llamaProcess.Start();
        _llamaProcess.BeginOutputReadLine();
        _llamaProcess.BeginErrorReadLine();
    }

    static void AddLogLine(string line)
    {
        lock (_logLock)
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > MaxLogLines)
                _logLines.Dequeue();
        }
    }

    static string GetLogText()
    {
        lock (_logLock)
        {
            _logBuilder.Clear();
            foreach (var line in _logLines)
                _logBuilder.AppendLine(line);
            return _logBuilder.ToString().TrimEnd();
        }
    }

    static void UpdateTooltip()
    {
        if (_trayIcon is null) return;
        // Windows tray tooltips are limited to 127 characters (strictly less than 128)
        var maxLen = 127 - "TinyLlama\n".Length;
        var log = GetLogText();
        if (log.Length > maxLen)
            log = "..." + log[(log.Length - maxLen + 3)..];
        _trayIcon.Text = "TinyLlama\n" + log;
    }

    static void ShowLog()
    {
        // Show full log in a message box (limited to last 30 lines)
        var log = GetLogText();
        MessageBox.Show(
            log,
            "TinyLlama Log",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    static void Shutdown()
    {
        KillLlama();
        _tooltipTimer?.Dispose();
        _trayIcon?.Dispose();
        Application.Exit();
    }

    static void KillLlama()
    {
        if (_llamaProcess is { HasExited: false })
        {
            try { _llamaProcess.Kill(); } catch { }
        }
    }
}
