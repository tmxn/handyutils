using System.Diagnostics;
using SttClient;
using SttClient.Config;
using SttClient.Recording;
using SttClient.Stt;

// stt-client — record a meeting (mic L + speakers R) and transcribe it via WhisperX.
// Commands: devices | record | transcribe <wav> | meeting | health

var cliArgs = Environment.GetCommandLineArgs()[1..];
var settings = Settings.Load();
Log.Init(settings.OutputDir);

if (cliArgs.Length == 0)
{
    // No args → interactive TUI (also the friendly behavior when double-clicked).
    return await SttClient.Tui.TuiApp.Run(settings);
}

switch (cliArgs[0].ToLowerInvariant())
{
    case "devices":
        return CmdDevices(settings);
    case "record":
        return CmdRecord(settings, cliArgs[1..]);
    case "transcribe":
        return await CmdTranscribe(settings, cliArgs[1..]);
    case "meeting":
        return CmdMeeting(settings, cliArgs[1..]);
    case "health":
        return await CmdHealth(settings);
    case "check":
        return await CmdCheck(settings);
    case "tui":
        return await SttClient.Tui.TuiApp.Run(settings);
    case "help":
    case "--help":
    case "-h":
        PrintUsage();
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command: {cliArgs[0]}");
        PrintUsage();
        return 1;
}

// ---------------------------------------------------------------- commands

static void PrintUsage()
{
    Console.WriteLine("""
        stt-client — record meetings (mic + speakers) and transcribe via WhisperX

        Run with no arguments for the interactive TUI.

        Commands:
          devices                          list microphone and all audio output devices
          record [--mic N] [--out FILE]
                 Captures all active audio output devices automatically.
                 [--rate 48000] [--no-keepalive]
                 Record L=mic, R=speakers to a 2ch WAV. Stop with Ctrl+C or "stop".
          transcribe <wav> [--model M] [--no-diarize] [--language en]
                 Upload and transcribe. Writes <wav>.transcript.txt + .transcript.json
          meeting                          record → confirm → transcribe, in one flow
          health                           check the configured server
          check                            self-check: server, devices, output dir
          tui                              interactive terminal UI
        """);
}

static int CmdDevices(Settings settings)
{
    var caps = AudioDevices.ListCaptures();
    var renders = AudioDevices.ListRenders();
    Console.WriteLine("Capture (microphone) devices:");
    foreach (var d in caps) Console.WriteLine("  " + d);
    Console.WriteLine();
    Console.WriteLine("Audio output devices captured by loopback:");
    foreach (var d in renders) Console.WriteLine("  " + d);
    Console.WriteLine();
    Console.WriteLine($"Configured: mic={settings.MicDevice?.ToString() ?? "default"}, loopback=all active outputs");
    return 0;
}

static async Task<int> CmdHealth(Settings settings)
{
    using var server = new SttServer(settings.ServerUrl);
    var health = await server.GetHealthAsync();
    Console.WriteLine($"{settings.ServerUrl} → {health}");
    return health == Health.Unreachable ? 1 : 0;
}

/// <summary>Startup self-check: configured URL + health, configured devices, output dir.</summary>
static async Task<int> CmdCheck(Settings settings)
{
    var ok = true;
    using var server = new SttServer(settings.ServerUrl);
    var health = await server.GetHealthAsync();
    Console.WriteLine($"Server   {settings.ServerUrl} → {health}");
    Log.Write($"check: server {settings.ServerUrl} → {health}");
    if (health == Health.Unreachable) ok = false;

    try
    {
        var caps = AudioDevices.ListCaptures();
        var name = settings.MicDevice is null ? "default"
            : settings.MicDevice < caps.Count ? caps[(int)settings.MicDevice].Name : $"INDEX {settings.MicDevice} (out of range)";
        if (settings.MicDevice >= caps.Count) ok = false;
        Console.WriteLine($"Mic      {name}");

        var renders = AudioDevices.ListRenders();
        Console.WriteLine($"Loopback {renders.Count} active output device(s)");
        foreach (var render in renders)
            Console.WriteLine($"         {render.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Devices  ERROR: {ex.Message}");
        ok = false;
    }

    var dir = Path.GetFullPath(settings.OutputDir);
    if (Directory.Exists(dir))
        Console.WriteLine($"Output   {dir}");
    else
    {
        Console.WriteLine($"Output   {dir}  (does not exist yet — will be created on record)");
    }
    Console.WriteLine($"Model    {settings.Model}  diarize={settings.Diarize}  language={settings.Language ?? "auto"}");
    Console.WriteLine(ok ? "OK" : "PROBLEMS FOUND (see above)");
    return ok ? 0 : 1;
}

static int CmdRecord(Settings settings, string[] rest)
{
    ParseRecordArgs(settings, rest, out int? micIdx, out string outPath,
        out int rate, out bool keepAlive);

    using var recorder = new Recorder(
        AudioDevices.GetCapture(micIdx),
        AudioDevices.GetRenders(),
        outPath, rate, keepAlive);

    Console.WriteLine($"Server: {settings.ServerUrl} (recording does not touch the server)");
    Console.WriteLine($"Recording L=mic, R=all audio outputs → {Path.GetFullPath(outPath)}");
    Console.WriteLine($"Capturing {recorder.LoopbackDeviceCount} active output device(s).");
    Console.WriteLine("Stop with Ctrl+C or type 'stop'.");

    var sw = Stopwatch.StartNew();
    var meter = new object();
    var meterText = new System.Text.StringBuilder();
    var meterThread = new Thread(() =>
    {
        while (true)
        {
            lock (meter)
            {
                Console.Write("\r\x1b[K" + meterText.ToString());
                Console.Out.Flush();
            }
            Thread.Sleep(500);
        }
    })
    { IsBackground = true };

    recorder.OnMeter += (total, lpeak, rpeak) =>
    {
        lock (meter)
        {
            var dur = TimeSpan.FromSeconds((double)total / rate);
            meterText.Clear()
                .Append($" {dur:hh\\:mm\\:ss}  L " + Meter(lpeak, 20) +
                        $"  R " + Meter(rpeak, 20) +
                        (recorder.MicFailed ? "  [mic LOST]" : "") +
                        (recorder.LoopbackFailed ? "  [loopback LOST]" : ""));
        }
    };

    var stopped = false;
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        stopped = true;
    };

    var lineThread = new Thread(() =>
    {
        while (!stopped)
        {
            var line = Console.ReadLine();
            if (line == null || line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
                stopped = true;
        }
    })
    { IsBackground = true };

    meterThread.Start();
    lineThread.Start();
    try
    {
        recorder.Start();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to start capture: {ex.Message}");
        Log.Write($"record FAILED to start: {ex.Message}");
        return 1;
    }
    Log.Write($"record start → {Path.GetFullPath(outPath)} (rate={rate}, keepalive={keepAlive}, " +
              $"mic={settings.MicDevice?.ToString() ?? "default"}, loopback=all active outputs)");

    while (!stopped) Thread.Sleep(100);
    recorder.Stop();
    lock (meter) Console.WriteLine();

    var info = new FileInfo(outPath);
    Log.Write($"record stop — {recorder.Duration:hh\\:mm\\:ss}, {info.Length / 1024.0 / 1024.0:F1} MB, " +
              $"micLost={recorder.MicFailed}, loopbackLost={recorder.LoopbackFailed}");
    Console.WriteLine($"Saved {Path.GetFullPath(outPath)} — {recorder.Duration:hh\\:mm\\:ss} " +
                      $"{info.Length / 1024.0 / 1024.0:F1} MB " +
                      $"{(recorder.MicFailed ? "(mic channel lost!)" : "")}" +
                      $"{(recorder.LoopbackFailed ? "(loopback channel lost!)" : "")}");
    return recorder.MicFailed || recorder.LoopbackFailed ? 2 : 0;
}

static string Meter(float peak, int width)
{
    var displayLevel = LevelMeter.ToDisplay(peak);
    int n = (int)Math.Clamp(displayLevel * width, 0, width);
    return new string('#', n) + new string('.', width - n) + $" {displayLevel,5:P0}";
}

static async Task<int> CmdTranscribe(Settings settings, string[] rest)
{
    if (rest.Length == 0 || rest[0].StartsWith('-')) return PrintUsageAndFail("transcribe <wav> [...]");
    var file = rest[0];
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"File not found: {file}");
        return 1;
    }
    string model = settings.Model;
    bool diarize = settings.Diarize;
    string? language = settings.Language;

    for (int i = 1; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--model": model = Next(rest, ref i); break;
            case "--no-diarize": diarize = false; break;
            case "--language": language = Next(rest, ref i); break;
            default: return PrintUsageAndFail($"unknown option for transcribe: {rest[i]}");
        }
    }

    var duration = SttServer.WavDuration(file);
    var size = new FileInfo(file).Length;
    Console.WriteLine($"Audio: {file} — {size / 1024.0 / 1024.0:F1} MB, {duration:hh\\:mm\\:ss}");

    using var server = new SttServer(settings.ServerUrl);
    var health = await server.GetHealthAsync();
    Log.Write($"transcribe {file}: server {settings.ServerUrl} → {health}");
    if (health == Health.Unreachable)
    {
        Console.Error.WriteLine($"Server {settings.ServerUrl} is UNREACHABLE. " +
                                "Check the network and 'serverUrl' in stt-client.json.");
        return 1;
    }
    if (health == Health.Busy)
    {
        Console.WriteLine("Server is BUSY (a job is in flight). The POST will queue behind it " +
                          "and just wait — continue? [Y/n] ");
        if (!Confirm()) return 1;
    }
    else
    {
        Console.WriteLine($"Server {settings.ServerUrl} is ok.");
    }

    var progress = new Progress<SttServer.Progress>(p =>
    {
        var pct = p.BytesTotal > 0 ? p.BytesDone * 100.0 / p.BytesTotal : 0;
        Console.Write($"\r\x1b[K {p.Phase,-6} {p.Elapsed:hh\\:mm\\:ss}  " +
                      $"{p.BytesDone / 1048576.0,8:F1} / {p.BytesTotal / 1048576.0:F1} MB " +
                      $"({pct,4:F0}%)  ETA ~{p.EstimatedTotal:hh\\:mm\\:ss}   ");
        Console.Out.Flush();
    });

    // Ctrl+C during the long wait cancels the request cleanly instead of killing the process.
    using var cts = new CancellationTokenSource();
    var userCancelled = false;
    Console.CancelKeyPress += (s, e) => { e.Cancel = true; userCancelled = true; cts.Cancel(); };

    const int maxAttempts = 3;
    for (int attempt = 1; ; attempt++)
    {
        Log.Write($"transcribe {file}: attempt {attempt}/{maxAttempts} " +
                  $"(model={model} diarize={diarize} language={language ?? "auto"})");
        try
        {
            var json = await server.TranscribeAsync(file, model, diarize, language, progress, cts.Token);
            Console.WriteLine("\r\x1b[K");

            var t = TranscriptRenderer.Parse(json);
            var baseName = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
            var txtPath = baseName + ".transcript.txt";
            var jsonPath = baseName + ".transcript.json";
            File.WriteAllText(txtPath, TranscriptRenderer.RenderText(t));
            File.WriteAllText(jsonPath, TranscriptRenderer.PrettyJson(json));
            Log.Write($"transcribe {file}: OK — {t.Segments.Count} segments, language={t.Language ?? "?"} " +
                      $"→ {txtPath}");

            Console.WriteLine($"Done. {t.Segments.Count} segments, language={t.Language ?? "?"}");
            Console.WriteLine($"  text: {txtPath}");
            Console.WriteLine($"  json: {jsonPath} (word-level timestamps + speakers)");
            return 0;
        }
        catch (SttServerError ex)
        {
            // The server answered — the detail body is the only record of the failure.
            // No retry: re-POSTing the same input gets the same error.
            Console.WriteLine("\r\x1b[K");
            Log.Write($"transcribe {file}: FAILED — HTTP {ex.StatusCode}: {ex.Body}");
            Console.Error.WriteLine($"HTTP {ex.StatusCode} — server said:");
            Console.Error.WriteLine(ex.Body);
            return 1;
        }
        catch (OperationCanceledException) when (userCancelled)
        {
            Console.WriteLine("\r\x1b[KCancelled by user. The upload is idempotent — just run it again.");
            Log.Write($"transcribe {file}: cancelled by user on attempt {attempt}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            // Timeout (or transport reset). The POST is idempotent — re-POST from the same WAV.
            Console.WriteLine("\r\x1b[KTimeout or connection lost.");
            Log.Write($"transcribe {file}: timed out / connection lost on attempt {attempt}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"\r\x1b[KNetwork error: {ex.Message}");
            Log.Write($"transcribe {file}: network error on attempt {attempt}: {ex.Message}");
        }

        if (attempt >= maxAttempts)
        {
            Console.Error.WriteLine($"Giving up after {maxAttempts} attempts.");
            return 1;
        }
        Console.WriteLine($"Retrying (attempt {attempt + 1}/{maxAttempts}) in 5 s — the re-POST is idempotent.");
        try { await Task.Delay(5000, cts.Token); }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\r\x1b[KCancelled by user.");
            return 1;
        }
    }
}

static string Next(string[] rest, ref int i)
{
    i++;
    if (i >= rest.Length) throw new ArgumentException($"missing value for {rest[i - 1]}");
    return rest[i];
}

static int CmdMeeting(Settings settings, string[] rest)
{
    int rc = CmdRecord(settings, rest);
    if (rc != 0)
    {
        if (rc == 2) Console.Error.WriteLine("A channel was lost; consider re-recording.");
        return rc;
    }
    if (!Confirm())
    {
        Console.WriteLine("Skipped transcription.");
        return 0;
    }
    // Find the file we just wrote (most recent in output dir)
    var dir = Path.GetFullPath(settings.OutputDir);
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"Output dir not found: {dir}");
        return 1;
    }
    var file = Directory.GetFiles(dir, "meeting-*.wav")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    if (file == null)
    {
        Console.Error.WriteLine($"No meeting-*.wav found in {dir}");
        return 1;
    }
    return CmdTranscribe(settings, new[] { file, }).GetAwaiter().GetResult();
}

// ---------------------------------------------------------------- helpers

static void ParseRecordArgs(Settings settings, string[] rest,
    out int? micIdx, out string outPath, out int rate, out bool keepAlive)
{
    micIdx = settings.MicDevice;
    outPath = Path.Combine(settings.OutputDir,
        $"meeting-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav");
    rate = 48000;
    keepAlive = true;

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--mic": micIdx = int.Parse(Next(rest, ref i)); break;
            case "--out": outPath = Next(rest, ref i); break;
            case "--rate": rate = int.Parse(Next(rest, ref i)); break;
            case "--no-keepalive": keepAlive = false; break;
            default: throw new ArgumentException($"unknown option for record: {rest[i]}");
        }
    }

    var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

    // Persist last-used device selection
    if (micIdx != settings.MicDevice)
    {
        settings.MicDevice = micIdx;
        settings.Save();
    }
}

static bool Confirm()
{
    Console.Write("Continue? [Y/n] ");
    var line = Console.ReadLine();
    return line == null || !line.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);
}

static int PrintUsageAndFail(string what)
{
    Console.Error.WriteLine($"Usage: stt-client {what}");
    return 1;
}
