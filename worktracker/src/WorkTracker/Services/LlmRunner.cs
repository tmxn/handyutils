using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WorkTracker.Services;

/// <summary>
/// Calls the configured LLM backend. The original process-backed pi integration is
/// retained, and llama.cpp uses its local OpenAI-compatible streaming endpoint.
/// Every call is logged to the raw/ directory (last 20 kept).
/// </summary>
public sealed class LlmRunner
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly LlmSettings _settings;

    public LlmRunner(LlmSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Resolves the command via PATH (Windows: also .cmd/.bat). Returns null if not found.</summary>
    public string? ResolveCommand()
    {
        if (!IsPiBackend()) return null;
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

    public bool IsReady() => IsPiBackend()
        ? ResolveCommand() != null
        : Uri.TryCreate(_settings.LlamaEndpoint, UriKind.Absolute, out var uri) &&
          (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<LlmResult> RunAsync(string prompt, CancellationToken ct = default,
        Action<string>? onOutput = null)
    {
        if (!IsPiBackend() && !IsReady())
            throw new LlmResolveError(
                $"llama.cpp endpoint '{_settings.LlamaEndpoint}' is not a valid absolute HTTP(S) URL. " +
                "Fix the endpoint in Settings.");
        return IsPiBackend()
            ? await RunProcessAsync(prompt, ct, onOutput)
            : await RunLlamaCppAsync(prompt, ct, onOutput);
    }

    private async Task<LlmResult> RunProcessAsync(string prompt, CancellationToken ct,
        Action<string>? onOutput)
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

        var stdoutTask = ReadOutputAsync(p.StandardOutput, onOutput, ct);
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

    private async Task<LlmResult> RunLlamaCppAsync(string prompt, CancellationToken ct,
        Action<string>? onOutput)
    {
        var endpoint = CompletionEndpoint(_settings.LlamaEndpoint);
        var args = new List<string> { "POST", endpoint.ToString() };
        var sw = Stopwatch.StartNew();
        var output = new StringBuilder();
        string stderr = "";
        var timedOut = false;
        var exitCode = 0;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));
            var reasoning = ReasoningOptions(_settings.LlamaThinkingLevel);
            var chatTemplateKwargs = new Dictionary<string, object>
            {
                ["enable_thinking"] = reasoning.EnableThinking,
            };
            var payload = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(_settings.LlamaModel) ? "any" : _settings.LlamaModel,
                ["messages"] = new[] { new { role = "user", content = prompt } },
                ["stream"] = true,
                ["reasoning_budget"] = reasoning.Budget,
                ["chat_template_kwargs"] = chatTemplateKwargs,
            };
            if (reasoning.Effort != null)
            {
                payload["reasoning_effort"] = reasoning.Effort;
                chatTemplateKwargs["reasoning_effort"] = reasoning.Effort;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload),
            };

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                stderr = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: " +
                         await response.Content.ReadAsStringAsync(timeoutCts.Token);
                exitCode = -1;
            }
            else
            {
                await ReadLlamaResponseAsync(response, output, onOutput, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            exitCode = -1;
            stderr = $"llama.cpp request timed out after {_settings.TimeoutSeconds}s";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            exitCode = -1;
            stderr = ex.Message;
        }

        sw.Stop();
        var result = new LlmResult
        {
            ExitCode = exitCode,
            Stdout = output.ToString(),
            Stderr = stderr,
            TimedOut = timedOut,
            Duration = sw.Elapsed,
        };
        AppLog.Info($"llm llama.cpp exit={result.ExitCode} " +
                    $"{(timedOut ? "TIMED OUT " : "")}{result.Duration.TotalSeconds:F1}s " +
                    $"prompt={prompt.Length}B stdout={result.Stdout.Length}B stderr={result.Stderr.Length}B " +
                    $"endpoint={endpoint}");
        RawLog.Write("llama.cpp " + endpoint, args, prompt, result);
        return result;
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string>? onOutput,
        CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            var chunk = new string(buffer, 0, count);
            output.Append(chunk);
            try { onOutput?.Invoke(chunk); } catch { /* UI observers must not break the LLM read. */ }
        }
        return output.ToString();
    }

    private static async Task ReadLlamaResponseAsync(HttpResponseMessage response, StringBuilder output,
        Action<string>? onOutput, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var isSse = response.Content.Headers.ContentType?.MediaType?.Contains("event-stream",
            StringComparison.OrdinalIgnoreCase) == true;

        if (!isSse)
        {
            var json = await reader.ReadToEndAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return;
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                Notify(GetText(message, "reasoning_content"), onOutput);
                Append(GetText(message, "content"), output, onOutput);
            }
            else
            {
                Append(GetText(choice, "text"), output, onOutput);
            }
            return;
        }

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            try
            {
                var (thinking, text) = ExtractDeltaText(data);
                Notify(thinking, onOutput);
                Append(text, output, onOutput);
            }
            catch (JsonException)
            {
                // Ignore a malformed keep-alive/event rather than losing the rest
                // of an otherwise usable stream.
            }
        }
    }

    private static (string Thinking, string Text) ExtractDeltaText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return ("", "");
        var delta = choices[0].TryGetProperty("delta", out var d) ? d : choices[0];
        // Some llama.cpp versions expose reasoning separately; include it in the
        // raw/live stream so the user can see what the model is doing.
        return (GetText(delta, "reasoning_content"),
            GetText(delta, "content") + GetText(delta, "text"));
    }

    private static string GetText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static void Append(string text, StringBuilder output, Action<string>? onOutput)
    {
        if (string.IsNullOrEmpty(text)) return;
        output.Append(text);
        try { onOutput?.Invoke(text); } catch { /* UI observers must not break the stream. */ }
    }

    private static void Notify(string text, Action<string>? onOutput)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { onOutput?.Invoke(text); } catch { /* UI observers must not break the stream. */ }
    }

    private static (int Budget, string? Effort, bool EnableThinking) ReasoningOptions(string? configured)
    {
        return configured?.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => (0, "none", false),
            "medium" => (2048, "medium", true),
            "high" => (8192, "high", true),
            "max" => (-1, null, true),
            // Keep older saved values meaningful while using the llama-server
            // levels and exact budgets supported by the endpoint.
            "xhigh" => (8192, "high", true),
            "minimal" => (512, "low", true),
            _ => (512, "low", true),
        };
    }

    private bool IsPiBackend() =>
        !string.Equals(_settings.Backend?.Trim(), "llama.cpp", StringComparison.OrdinalIgnoreCase);

    private static Uri CompletionEndpoint(string configured)
    {
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("llama.cpp endpoint must be an absolute http:// or https:// URL.");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return uri;
        return new Uri(uri, path + "/v1/chat/completions");
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
