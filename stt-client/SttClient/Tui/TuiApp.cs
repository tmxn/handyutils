using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Rendering;
using SttClient.Config;
using SttClient.Recording;
using SttClient.Stt;

namespace SttClient.Tui;

/// <summary>
/// Interactive TUI layer (Phase 4) on top of the console core:
/// home screen with server status, device picker, live recording display,
/// transcribe progress, transcript pager, and config editing.
///
/// API notes (Spectre.Console 0.57.2, verified by reflection — the docs are stale):
/// - there is NO AnonConsoleApp/AnonConsole; everything goes through static AnsiConsole;
/// - prompts are plain classes: SelectionPrompt/TextPrompt with a DefaultValue property,
///   shown via prompt; Esc throws OperationCanceledException;
/// - simple yes/no: AnsiConsole.Confirm(message, defaultAnswer);
/// - live display: AnsiConsole.Live(renderable).AutoClear().Start(ctx => ...) with ctx.UpdateTarget(...);
/// - no async key API: read keys with Console.ReadKey(true) on a worker thread while Live runs;
/// - progress: AnsiConsole.Progress().StartAsync(ctx => ...), ctx.AddTask(name, autoStart, maxValue),
///   task.IsIndeterminate for the "server is working" phase.
/// </summary>
public static class TuiApp
{
    private const int HomeRecord = 0, HomeMeeting = 1, HomeTranscribe = 2,
        HomeDevices = 3, HomeConfig = 4, HomeExit = 5;

    /// <summary>
    /// 0.57.2 SelectionPrompt labels come from the value's ToString() (no WithLabel/Choice),
    /// so menu items are small records with a readable ToString.
    /// </summary>
    private sealed record Option(int Id, string Label) { public override string ToString() => Label; }
    private sealed record DeviceOption(int? Index, string Label) { public override string ToString() => Label; }

    public static async Task<int> Run(Settings settings)
    {
        while (true)
        {
            int action;
            try
            {
                action = await ShowHomeAsync(settings);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[dim]Bye.[/]");
                return 0; // Esc on the home screen
            }
            AnsiConsole.MarkupLine("");

            try
            {
                switch (action)
                {
                    case HomeRecord:
                        await RecordAsync(settings);
                        break;
                    case HomeMeeting:
                        var file = await RecordAsync(settings);
                        if (file != null && AnsiConsole.Confirm(
                            $"Transcribe {Path.GetFileName(file)} now? (you can also run 'transcribe' later)", false))
                        {
                            await TranscribeAsync(settings, file);
                        }
                        break;
                    case HomeTranscribe:
                        await TranscribeAsync(settings, await PickFileAsync(settings));
                        break;
                    case HomeDevices:
                        await DevicesAsync(settings);
                        break;
                    case HomeConfig:
                        await ConfigAsync(settings);
                        break;
                    default:
                        AnsiConsole.MarkupLine("[dim]Bye.[/]");
                        return 0;
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            }
            AnsiConsole.MarkupLine("");
        }
    }

    // ---------------------------------------------------------------- home

    private static async Task<int> ShowHomeAsync(Settings settings)
    {
        Health health;
        using (var server = new SttServer(settings.ServerUrl))
            health = await server.GetHealthAsync();

        var badge = health switch
        {
            Health.Ok => "[green]● ok[/]",
            Health.Busy => "[yellow]● busy[/]",
            _ => "[red]● unreachable[/]",
        };
        AnsiConsole.MarkupLine($"[bold]stt-client[/]  [dim]server[/] {settings.ServerUrl} {badge}");
        AnsiConsole.MarkupLine($"[dim]model {settings.Model} · diarize {settings.Diarize} · " +
                               $"language {settings.Language ?? "auto"} · output {settings.ResolvedOutputDir}[/]");
        var choices = new[]
        {
            new Option(HomeRecord, "record     — capture a meeting (L=mic, R=all outputs)"),
            new Option(HomeMeeting, "meeting    — record, then transcribe"),
            new Option(HomeTranscribe, "transcribe — upload a WAV to the server"),
            new Option(HomeDevices, "devices    — pick microphone (all outputs are automatic)"),
            new Option(HomeConfig, "config     — server URL, model, output dir, ..."),
            new Option(HomeExit, "exit"),
        };
        var choice = AnsiConsole.Prompt(new SelectionPrompt<Option>()
            .Title("What do you want to do?")
            .PageSize(7)
            .AddChoices(choices)
            .DefaultValue(choices[0]));
        return choice.Id;
    }

    // ---------------------------------------------------------------- devices

    private static async Task DevicesAsync(Settings settings)
    {
        var caps = AudioDevices.ListCaptures();
        var renders = AudioDevices.ListRenders();

        AnsiConsole.MarkupLine("[bold]Capture (microphone) devices:[/]");
        foreach (var d in caps)
        {
            var mark = d.IsDefault ? "[green]*[/]" : " ";
            var conf = settings.MicDevice == d.Index ? "[cyan] ← configured[/]" : "";
            AnsiConsole.MarkupLine($"  [dim]{d.Index,3}[/] {mark} {d.Name} [dim]({d.Format})[/]{conf}");
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Audio output devices captured by loopback:[/]");
        foreach (var d in renders)
        {
            var mark = d.IsDefault ? "[green]*[/]" : " ";
            AnsiConsole.MarkupLine($"  [dim]{d.Index,3}[/] {mark} {d.Name} [dim]({d.Format})[/]");
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]* = system default device. The recording automatically captures " +
                               "all active outputs, so no headphone selection is needed.[/]");
        AnsiConsole.WriteLine();

        var micChoices = new[] { new DeviceOption(null, "system default") }
            .Concat(caps.Select(d => new DeviceOption(d.Index, d.Name))).ToList();
        var mic = AnsiConsole.Prompt(new SelectionPrompt<DeviceOption>()
            .Title("Microphone (left channel)")
            .PageSize(10)
            .AddChoices(micChoices)
            .DefaultValue(micChoices.FirstOrDefault(c => c.Index == settings.MicDevice) ?? micChoices[0]));

        settings.MicDevice = mic.Index;
        settings.Save();
        Log.Write($"devices: mic={mic.Index?.ToString() ?? "default"}, loopback=all active outputs");
        AnsiConsole.MarkupLine($"[green]Saved.[/] mic = {mic.Label}, all active audio outputs will be captured " +
                               "[dim](stt-client.json in the working directory)[/]");
    }

    // ---------------------------------------------------------------- record

    /// <summary>Live recording display. Returns the WAV path, or null if aborted/failed.</summary>
    private static Task<string?> RecordAsync(Settings settings)
    {
        var dir = settings.ResolvedOutputDir;
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"meeting-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav");
        const int rate = 48000;

        var recorder = new SttClient.Recording.Recorder(
            AudioDevices.GetCapture(settings.MicDevice),
            AudioDevices.GetRenders(),
            outPath, rate, keepAlive: true);

        // Latest meter values (written from the NAudio callback thread, read by the render loop).
        long total = 0;
        float leftPeak = 0, rightPeak = 0;
        recorder.OnMeter += (t, l, r) => { total = t; leftPeak = l; rightPeak = r; };

        var stopped = false;
        var cts = new CancellationTokenSource();
        void OnCancel(object? s, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stopped = true;
            cts.Cancel();
        }
        Console.CancelKeyPress += OnCancel;

        try
        {
            var live = AnsiConsole.Live(new Markup("[bold]Starting capture...[/]")).AutoClear(true);
            live.Start(ctx =>
            {
                Thread? keyThread = null;
                try
                {
                    Power.KeepAwake(); // don't let Windows sleep mid-meeting
                    recorder.Start();
                }
                catch (Exception ex)
                {
                    Power.Release();
                    recorder.Dispose();
                    AnsiConsole.MarkupLine($"[red]Failed to start capture:[/] {ex.Message}");
                    Log.Write($"record (tui) FAILED to start: {ex.Message}");
                    return;
                }
                Log.Write($"record start (tui) → {outPath} (rate={rate}, " +
                          $"mic={settings.MicDevice?.ToString() ?? "default"}, loopback=all active outputs)");

                // Spectre has no async key API: read keys on a worker thread while Live renders.
                keyThread = new Thread(() =>
                {
                    while (!stopped)
                    {
                        ConsoleKeyInfo k;
                        try { k = Console.ReadKey(true); }
                        catch (OperationCanceledException) { stopped = true; return; }
                        if (k.Key is ConsoleKey.Escape or ConsoleKey.Spacebar or ConsoleKey.Q)
                        {
                            stopped = true;
                            return;
                        }
                    }
                }) { IsBackground = true };
                keyThread.Start();

                try
                {
                    while (!stopped)
                    {
                        ctx.UpdateTarget(BuildRecRender(recorder, total, leftPeak, rightPeak, outPath, rate));
                        Thread.Sleep(250);
                    }
                }
                finally
                {
                    stopped = true; // let the key thread exit too
                    recorder.Stop();
                    keyThread.Join(TimeSpan.FromSeconds(3));
                    Power.Release();
                }
            });

            recorder.Dispose();

            double sizeMb = 0;
            try { sizeMb = new FileInfo(outPath).Length / 1048576.0; } catch { }
            Log.Write($"record stop (tui) — {recorder.Duration:hh\\:mm\\:ss}, {sizeMb:F1} MB, " +
                      $"micLost={recorder.MicFailed}, loopbackLost={recorder.LoopbackFailed}");
            AnsiConsole.MarkupLine($"[green]Saved[/] {outPath}  [dim]{recorder.Duration:hh\\:mm\\:ss}, {sizeMb:F1} MB[/]");
            if (recorder.MicFailed || recorder.LoopbackFailed)
                AnsiConsole.MarkupLine("[yellow]A channel was lost — consider re-recording.[/]");
            return Task.FromResult<string?>(outPath);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            cts.Dispose();
        }
    }

    private static IRenderable BuildRecRender(SttClient.Recording.Recorder recorder, long total, float l, float r, string path, int rate)
    {
        var dur = TimeSpan.FromSeconds((double)total / rate);
        double sizeMb = 0;
        try { sizeMb = new FileInfo(path).Length / 1048576.0; } catch { }
        return new Markup(
            $"[bold]Recording[/] [blue]{dur:hh\\:mm\\:ss}[/]  [dim]{sizeMb,6:F1} MB[/]  →  {path}\n" +
            $"  L mic       {Bar(l, 30, "blue")}" + (recorder.MicFailed ? "  [red][mic LOST — continuing on speakers][/]" : "") + "\n" +
            $"  R all outputs {Bar(r, 30, "green")}" + (recorder.LoopbackFailed ? "  [red][loopback LOST — continuing on mic][/]" : "") + "\n" +
            $"[dim]Esc / Space / Q to stop · Ctrl+C also stops[/]");
    }

    /// <summary>ASCII level meter as markup.</summary>
    private static string Bar(float peak, int width, string color)
    {
        var displayLevel = LevelMeter.ToDisplay(peak);
        int n = (int)Math.Clamp(displayLevel * width, 0, width);
        return $"[{color}]{new string('#', n)}[/][dim]{new string('·', width - n)}[/] [dim]{displayLevel,5:P0}[/]";
    }

    // ---------------------------------------------------------------- transcribe

    private static async Task TranscribeAsync(Settings settings, string? file)
    {
        file ??= await PickFileAsync(settings);
        if (file == null) return;

        var duration = SttServer.WavDuration(file);
        var size = new FileInfo(file).Length;
        var model = settings.Model;
        var diarize = settings.Diarize;
        var language = settings.Language;

        using var server = new SttServer(settings.ServerUrl);
        var health = await server.GetHealthAsync();
        Log.Write($"transcribe (tui) {file}: server {settings.ServerUrl} → {health}");
        if (health == Health.Unreachable)
        {
            AnsiConsole.MarkupLine($"[red]Server {settings.ServerUrl} is UNREACHABLE.[/] Fix it in 'config'.");
            return;
        }
        if (health == Health.Busy &&
            !AnsiConsole.Confirm("Server is BUSY (a job is in flight). The POST will queue behind it and just wait — continue?", true))
        {
            return;
        }

        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            Log.Write($"transcribe (tui) {file}: attempt {attempt}/{maxAttempts} " +
                      $"(model={model} diarize={diarize} language={language ?? "auto"})");
            string? json = null;
            SttServerError? serverError = null;
            string? networkError = null;
            bool userCancelled = false;

            using var cts = new CancellationTokenSource();
            void OnCancel(object? s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cts.Cancel();
            }
            Console.CancelKeyPress += OnCancel;
            try
            {
                var display = AnsiConsole.Progress()
                    .Columns(new ProgressColumn[]
                    {
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new ElapsedTimeColumn(),
                        new RemainingTimeColumn(),
                    });

                await display.StartAsync(async ctx =>
                {
                    var uploadTask = ctx.AddTask($"upload {Path.GetFileName(file)}", true, size);
                    var serverTask = ctx.AddTask("server — waiting (do not interrupt)", false, 0);
                    serverTask.IsIndeterminate = true;

                    var progress = new Progress<SttServer.Progress>(p =>
                    {
                        if (p.Phase == "upload")
                        {
                            uploadTask.Value = p.BytesDone;
                        }
                        else
                        {
                            if (!uploadTask.IsFinished)
                            {
                                uploadTask.Value = uploadTask.MaxValue;
                                uploadTask.StopTask();
                            }
                            if (!serverTask.IsStarted) serverTask.StartTask();
                            serverTask.Description =
                                $"server — {p.Elapsed:hh\\:mm\\:ss} elapsed, total ETA ~{p.EstimatedTotal:hh\\:mm\\:ss}";
                        }
                    });

                    try
                    {
                        json = await server.TranscribeAsync(file, model, diarize, language, progress, cts.Token);
                    }
                    catch (SttServerError ex)
                    {
                        serverError = ex;
                        Log.Write($"transcribe (tui) {file}: FAILED — HTTP {ex.StatusCode}: {ex.Body}");
                    }
                    catch (OperationCanceledException)
                    {
                        userCancelled = cts.IsCancellationRequested;
                        Log.Write($"transcribe (tui) {file}: {(userCancelled ? "cancelled by user" : "timed out")} on attempt {attempt}");
                    }
                    catch (HttpRequestException ex)
                    {
                        networkError = ex.Message;
                        Log.Write($"transcribe (tui) {file}: network error on attempt {attempt}: {ex.Message}");
                    }
                });
            }
            finally
            {
                Console.CancelKeyPress -= OnCancel;
                cts.Dispose();
            }

            if (json != null)
            {
                var t = TranscriptRenderer.Parse(json);
                var baseName = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
                var txtPath = baseName + ".transcript.txt";
                var jsonPath = baseName + ".transcript.json";
                File.WriteAllText(txtPath, TranscriptRenderer.RenderText(t));
                File.WriteAllText(jsonPath, TranscriptRenderer.PrettyJson(json));
                Log.Write($"transcribe (tui) {file}: OK — {t.Segments.Count} segments → {txtPath}");

                AnsiConsole.MarkupLine($"[green]Done.[/] [dim]{t.Segments.Count} segments, language={t.Language ?? "?"}[/]");
                AnsiConsole.MarkupLine($"  [dim]{txtPath}[/]");
                AnsiConsole.MarkupLine($"  [dim]{jsonPath} (word-level timestamps + speakers)[/]");
                if (AnsiConsole.Confirm("View the transcript now?", true))
                    await ShowTranscriptAsync(txtPath);
                return;
            }

            if (serverError != null)
            {
                // The server answered — the body is the only record of the failure. No retry.
                AnsiConsole.MarkupLine($"[red]HTTP {serverError.StatusCode} — server said:[/]");
                AnsiConsole.WriteLine(serverError.Body);
                return;
            }
            if (userCancelled)
            {
                AnsiConsole.MarkupLine("[yellow]Cancelled.[/] The upload is idempotent — just run it again.");
                return;
            }
            if (attempt >= maxAttempts)
            {
                var giveUp = "[red]Giving up after " + maxAttempts + " attempts." +
                             (networkError != null ? $" ({networkError})" : "") + "[/]";
                AnsiConsole.MarkupLine(giveUp);
                return;
            }
            var failedHow = networkError != null ? $" (network: {networkError})" : " (timeout)";
            AnsiConsole.MarkupLine($"[yellow]Attempt {attempt} failed{failedHow}. " +
                                   "Retrying in 5 s — the re-POST is idempotent.[/]");
            await Task.Delay(5000);
        }
    }

    private static async Task<string?> PickFileAsync(Settings settings)
    {
        var dir = settings.ResolvedOutputDir;
        var wavs = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.wav")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(30)
                .ToList()
            : new List<string>();

        var fileChoices = new[] { new Option(0, "(type a path)") }
            .Concat(wavs.Select((w, i) => new Option(i + 1, Path.GetFileName(w)))).ToList();
        var choice = AnsiConsole.Prompt(new SelectionPrompt<Option>()
            .Title("Which WAV to transcribe?")
            .PageSize(25)
            .AddChoices(fileChoices)
            .DefaultValue(fileChoices[0]));

        if (choice.Id > 0) return wavs[choice.Id - 1];
        var typed = AnsiConsole.Prompt(new TextPrompt<string>("WAV path").AllowEmpty());
        typed = typed.Trim().Trim('"');
        if (typed.Length == 0 || !File.Exists(typed))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {typed}");
            return null;
        }
        return typed;
    }

    // ---------------------------------------------------------------- transcript pager

    private static Task ShowTranscriptAsync(string path)
    {
        List<string> lines;
        try
        {
            lines = File.ReadAllLines(path).ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Cannot read transcript:[/] {ex.Message}");
            return Task.CompletedTask;
        }

        int perPage = Math.Max(4, Console.WindowHeight - 5);
        int start = 0;
        while (start < lines.Count)
        {
            // Plain Text renderable: brackets in timestamps/speech must not hit the markup parser.
            AnsiConsole.Write(new Text(string.Join("\n", lines.Skip(start).Take(perPage))));
            AnsiConsole.MarkupLine($"[dim]-- {Math.Min(start + perPage, lines.Count)}/{lines.Count} · " +
                                    "Space/Enter/→: next · Q/Esc: quit[/]");
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key is ConsoleKey.Escape or ConsoleKey.Q) return Task.CompletedTask;
                if (k.Key is ConsoleKey.Spacebar or ConsoleKey.Enter or ConsoleKey.RightArrow) break;
            }
            start += perPage;
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- config

    private static async Task ConfigAsync(Settings settings)
    {
        var url = AnsiConsole.Prompt(new TextPrompt<string>("Server URL").AllowEmpty());
        url = url.Trim().TrimEnd('/');
        if (url.Length > 0)
        {
            Health health;
            using (var probe = new SttServer(url))
                health = await probe.GetHealthAsync();
            var badge = health switch
            {
                Health.Ok => "[green]● ok[/]",
                Health.Busy => "[yellow]● busy[/]",
                _ => "[red]● unreachable[/]",
            };
            AnsiConsole.MarkupLine($"  [dim]health:[/] {badge}");
            if (health == Health.Unreachable && !AnsiConsole.Confirm("Server is unreachable — save the URL anyway?", false))
                return;
            settings.ServerUrl = url;
        }

        settings.Model = AnsiConsole.Prompt(new TextPrompt<string>("Model").AllowEmpty());
        settings.Diarize = AnsiConsole.Confirm("Diarize speakers? (labels like SPEAKER_00)", settings.Diarize);
        var lang = AnsiConsole.Prompt(new TextPrompt<string>("Language (blank = auto-detect)").AllowEmpty());
        settings.Language = string.IsNullOrWhiteSpace(lang) ? null : lang.Trim();
        var outDir = AnsiConsole.Prompt(new TextPrompt<string>("Output dir (blank keeps current)").AllowEmpty());
        if (outDir.Length > 0) settings.OutputDir = outDir;

        settings.Save();
        Log.Init(settings.ResolvedOutputDir); // log now points at the (possibly new) output dir
        Log.Write($"config saved: server={settings.ServerUrl} model={settings.Model} " +
                  $"diarize={settings.Diarize} language={settings.Language ?? "auto"} output={settings.ResolvedOutputDir}");
        AnsiConsole.MarkupLine($"[green]Saved to {Path.Combine(Settings.DataDirectory, Settings.FileName)}.[/]");
    }
}

/// <summary>
/// Windows power: prevent the system from sleeping while recording
/// (SetThreadExecutionState with ES_CONTINUOUS | ES_SYSTEM_REQUIRED).
/// </summary>
internal static class Power
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    public static void KeepAwake() => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
    public static void Release() => SetThreadExecutionState(ES_CONTINUOUS);
}
