using System.Diagnostics;
using System.IO;

namespace WorkTracker.Services;

/// <summary>
/// Spawns the configured external LLM command with the prompt on stdin and reads JSON from stdout.
/// No authentication or model handling happens here — that is the user's CLI configuration.
/// Every call is logged to the raw/ directory (last 20 kept).
/// </summary>
public sealed class LlmRunner
{
    private readonly LlmSettings _settings;

    public LlmRunner(LlmSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Resolves the command via PATH (Windows: also .cmd/.bat). Returns null if not found.</summary>
    public string? ResolveCommand()
    {
        var name = _settings.Command;
        if (string.IsNullOrWhiteSpace(name)) return null;
        // Already an absolute/relative path?
        if (name.Contains('/') || name.Contains('\\') || Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        // Windows: prefer real executables and cmd shims; extensionless files in npm dirs
        // are POSIX shell shims that CreateProcess cannot launch.
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* skip bad PATH entries */ }
            }
        }
        return null;
    }

    public async Task<LlmResult> RunAsync(string prompt, CancellationToken ct = default)
    {
        var resolved = ResolveCommand();
        if (resolved == null)
        {
            AppLog.Error($"llm command '{_settings.Command}' not found on PATH");
            throw new LlmResolveError(
                $"LLM command '{_settings.Command}' was not found on PATH." +
                (OperatingSystem.IsWindows() ? $" Also tried '{_settings.Command}.cmd'." : "") +
                " Fix the LLM command in Settings.");
        }

        var psi = new ProcessStartInfo(resolved)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in EffectiveArgs(resolved)) psi.ArgumentList.Add(a);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.Start();

        using var stdin = p.StandardInput;
        await stdin.WriteAsync(prompt);
        stdin.Close();

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
        var finished = await Task.WhenAny(Task.Run(() => p.WaitForExit(), ct), Task.Delay(timeout, ct));
        bool timedOut = finished == null || !finished.IsCompletedSuccessfully;
        if (timedOut)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!p.HasExited)
        {
            try { p.WaitForExit(2000); } catch { }
        }
        sw.Stop();

        var result = new LlmResult
        {
            ExitCode = p.HasExited ? p.ExitCode : -1,
            Stdout = stdout,
            Stderr = stderr,
            TimedOut = timedOut,
            Duration = sw.Elapsed,
        };
        var effectiveArgs = EffectiveArgs(resolved);
        AppLog.Info($"llm {Path.GetFileName(resolved)} exit={result.ExitCode} " +
                    $"{(timedOut ? "TIMED OUT " : "")}{result.Duration.TotalSeconds:F1}s " +
                    $"prompt={prompt.Length}B stdout={result.Stdout.Length}B stderr={result.Stderr.Length}B " +
                    $"args={string.Join(' ', effectiveArgs)}");
        RawLog.Write(resolved, effectiveArgs, prompt, result);
        return result;
    }

    /// <summary>
    /// Returns the args to launch with. When the resolved command is pi, injects the
    /// configured --thinking level (removing any --thinking the user already put in the
    /// args box so the two can't conflict). For non-pi commands nothing is injected.
    /// </summary>
    private List<string> EffectiveArgs(string resolved)
    {
        var fname = Path.GetFileNameWithoutExtension(resolved);
        var isPi = fname.Equals("pi", StringComparison.OrdinalIgnoreCase) ||
                   fname.StartsWith("pi.", StringComparison.OrdinalIgnoreCase);
        if (isPi && !string.IsNullOrWhiteSpace(_settings.ThinkingEffort))
        {
            var args = new List<string>();
            for (var i = 0; i < _settings.Args.Count; i++)
            {
                if (_settings.Args[i] == "--thinking")
                {
                    i++; // skip the value of the "--thinking x" form
                }
                else if (_settings.Args[i].StartsWith("--thinking="))
                {
                    continue; // --thinking=x form: skip it
                }
                else
                {
                    args.Add(_settings.Args[i]);
                }
            }
            args.Add("--thinking");
            args.Add(_settings.ThinkingEffort.Trim().TrimStart('-'));
            return args;
        }
        return _settings.Args;
    }
}

/// <summary>Keeps the last 20 raw LLM exchanges in %USERPROFILE%\WorkTrackerData\raw.</summary>
internal static class RawLog
{
    private const int MaxRawFiles = 20;

    public static void Write(string command, List<string> args, string prompt, LlmResult result)
    {
        try
        {
            var dir = ConfigStore.RawDir;
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"command: {command}");
            sb.AppendLine("args: " + string.Join(' ', args));
            sb.AppendLine($"exitCode: {result.ExitCode}");
            sb.AppendLine($"timedOut: {result.TimedOut}");
            sb.AppendLine($"duration: {result.Duration.TotalSeconds:F1}s");
            sb.AppendLine();
            sb.AppendLine("=== STDIN (prompt) ===");
            sb.AppendLine(prompt);
            sb.AppendLine();
            sb.AppendLine("=== STDOUT ===");
            sb.AppendLine(result.Stdout);
            sb.AppendLine();
            sb.AppendLine("=== STDERR (tail) ===");
            sb.AppendLine(result.Stderr.Length > 2000 ? result.Stderr[^2000..] : result.Stderr);

            var path = Path.Combine(dir, $"{stamp}-wt.txt");
            File.WriteAllText(path, sb.ToString());

            // Prune oldest beyond MaxRawFiles.
            var files = Directory.GetFiles(dir, "*-wt.txt")
                .OrderBy(f => f).ToArray();
            foreach (var old in files.Take(Math.Max(0, files.Length - MaxRawFiles)))
                File.Delete(old);
        }
        catch
        {
            // Raw logging must never break the main flow.
        }
    }
}
