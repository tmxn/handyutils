# STT Client — TODO / Plan

## Status (console core — Phases 0–2)

**Implemented and end-to-end verified against the real server.**
- `dotnet build` succeeds (net8.0, win-x64, NAudio 2.2.1).
- `devices` enumerates mic + loopback render devices.
- `record` captures L = mic and automatically captures all active render devices
  into the mixed R channel of one 2ch 48 kHz 16-bit WAV; both channels confirmed
  live (mic peak 0.82, loopback RMS 0.10 when audio played).
  Underruns padded with silence; keep-alive silence keeps the loopback alive.
- `transcribe` uploads (streamed, no memory blow-up), polls nothing, reads the
  500 body, and writes `*.transcript.txt` (timestamped, speaker-labeled, leading
  spaces stripped) + `*.transcript.json` (raw word-level data). Verified on real
  speech. By default, recordings, transcripts, and logs go under `~/.stt-client`.
- Known gotchas fixed during testing: WAV duration parses only after `RIFF <size>`
  + `WAVE`; the `/health` timeout is isolated from the long POST (infinite client
  timeout + per-call cancellation).

### Verified checklist (Phases 0–2)

- [x] `dotnet new console` → `SttClient/`, .NET 8
- [x] NuGet: `NAudio`
- [x] Solution layout: `SttClient/` (Program.cs), `Recording/`, `Stt/`, `Config/`
- [x] `~/.stt-client/stt-client.json` config load/save (independent of launcher working directory)
- [x] `devices` enumeration (index, default, format)
- [x] Dual-stream capture → interleaved 2ch WAV; common rate; per-channel silence padding
- [x] `record` (stop on Ctrl+C or `stop`); header flush on dispose
- [x] Level meters (peak per channel)
- [x] `--no-keepalive`; loopback keep-alive
- [x] `SttServer.GetHealth()` → ok/busy/unreachable
- [x] `TranscribeAsync` multipart (streamed file), no/long timeout, read 500 body
- [x] CLI `transcribe <wav> [--no-diarize] [--language en] [--model large-v3]`
- [x] Pre-flight `/health`; progress (elapsed + ETA from FINDINGS.md)
- [x] Writes `.transcript.txt` + `.transcript.json` on success; prints `detail` on failure
- [x] `meeting` = record → confirm → transcribe

### Not yet done

- [x] Phase 3 hardening (self-check via `check` command, auto-retry on network/timeout
      failures ×3 with idempotent re-POST, config persistence of last devices, Ctrl+C
      cancels upload cleanly, logging to `stt-client.log` in the output dir)
- [x] Store config, recordings, transcripts, and logs under `~/.stt-client` by default
- [x] Single-file self-contained publish: `publish/stt-client.exe` (68 MB, runtime bundled, runs standalone)
- [x] Phase 4 TUI (`stt-client tui`, Spectre.Console 0.57.2): home screen with server
      status badge, device picker (persists), live record display (meters, size, Esc/Space
      to stop, keep-awake), transcribe progress (upload bar + indeterminate server phase,
      retry ×3), meeting flow, transcript pager, config editor with inline health test.
      NOTE: verified by build + reflection against 0.57.2 (no AnonConsoleApp exists;
      AnsiConsole.Prompt is sync; labels come from value.ToString() → Option records);
      TUI itself not yet exercised interactively end-to-end.

### Publish

Re-publish after changes: `dotnet publish SttClient -c Release -r win-x64 --self-contained
-p:PublishSingleFile=true` → `publish/stt-client.exe` (the checked-in binary predates
Phase 3/4).

C# application: records mic + speakers (WASAPI loopback) for a meeting, then on
demand uploads to the WhisperX server and produces a transcript. See `FINDINGS.md`
for verified server behavior, timing, and response schema.

## Decisions (locked)

- **Language/platform:** C# / .NET 8, Windows.
- **Capture:** NAudio — `WasapiCapture` (mic) + one `WasapiLoopbackCapture` for
  each active render device, mixing all render streams into one 2-channel WAV
  (L = mic, R = all outputs) at a common sample rate (target 48 kHz; resample
  streams if devices differ). This avoids choosing the currently active headphone.
- **Format:** 16-bit PCM WAV. No compression.
- **Server URL:** configurable (`--server` flag and/or
  `~/.stt-client/stt-client.json`), default `http://10.11.12.14:8000`.
  Validate via `GET /health`
  at startup: distinguish unreachable / down / busy.
- **Phasing:** plain console core first (usable end-to-end), then a TUI layer
  on top (Spectre.Console — live displays, level meters, progress, transcript
  pager). No WPF/GUI.

## Phase 0 — scaffold

- [ ] `dotnet new console` → `stt-client/`, .NET 8, x64
- [ ] NuGet: `NAudio`, `System.Text.Json` (built-in)
- [ ] Solution layout:
  - `SttClient/` — app (Program.cs, command parsing)
  - `SttClient/Recording/` — device enumeration, capture, WAV writer
  - `SttClient/Stt/` — HTTP client, health, upload, transcript rendering
  - `SttClient/Config/` — settings load/save
- [ ] `stt-client.json` config: `serverUrl`, `model`, `diarize`, `language`,
  `micDevice`, `outputDir` (all active render devices are captured automatically)

## Phase 1 — recording core (console)

- [ ] List capture devices (`MMDeviceEnumerator`): show index, name, default flag;
  select the mic by index or name from config/args; capture every active render
  device automatically
- [ ] Open both streams; verify/negotiate common sample rate + 16-bit + channel
  counts (loopback comes in as 2ch typically → map to R, mic 1ch → L; pad/convert
  as needed)
- [ ] Write `meeting-<timestamp>.wav`, 2ch, interleaved, via `WaveFileWriter`
  (buffer both streams into one writer; handle one stream underrunning without
  killing the other — write silence)
- [ ] CLI: `record` (start), stop on Ctrl+C or `stop` input → flush WAV header,
  print path + duration
- [ ] Level meters (peak per channel, simple text) so the user can verify both
  sides are actually capturing before the meeting starts
- [ ] Edge cases: device unplugged/changed mid-recording → warn, keep going on the
  other channel; loopback silent when no audio playing (WASAPI loopback quirk —
  document that the app itself may need to play silence to keep the render stream
  alive)
- [ ] Manual test: record 30 s with mic + a YouTube video on speakers, verify WAV
  (play it back; confirm R channel has the video, L has the voice)

## Phase 2 — STT client core (console)

- [ ] `SttClient.Http.SttServer` wrapper:
  - `GetHealth()` → `ok | busy | unreachable` (short timeout, single attempt)
  - `Transcribe(file, model, diarize, language)` — multipart POST, **no/long
    timeout** (≥ 30 min or scaled to file duration), read 500 body into error
  - Never poll/probe the server mid-upload; single blocking request
- [ ] Multipart builder: `file` part (streamed from disk, not loaded into memory —
  files can be 600+ MB) + form fields
- [ ] CLI: `transcribe <wav> [--no-diarize] [--language en] [--model large-v3]`
  - pre-flight: `/health` → if `busy`, warn and ask/queue (server serializes; the
    POST will just wait)
  - progress: show elapsed + estimated remaining (calibrate: ~1 min/hour upload on
    10 GbE, ~6 min/hour inference — from FINDINGS.md)
  - on success: write `<wav-basename>.transcript.txt` (timestamped, speaker
    labeled, strip leading spaces) **and** `<wav-basename>.transcript.json`
    (raw response — keeps word-level timestamps/speakers)
  - on failure: print `detail` body verbatim; exit non-zero
- [ ] Combined command: `meeting` = record → stop → confirm → transcribe → save

## Phase 3 — hardening

- [ ] Startup self-check: server reachable + `ok`; show configured URL
- [ ] Resume/retry: if the upload request dies (network blip), re-POST from the
  same WAV (idempotent — server discards its scratch after each job)
- [ ] Config file persistence of last-used devices + server URL
- [ ] Graceful shutdown on Ctrl+C (flush WAV, cancel upload cleanly)
- [ ] Logging to `stt-client.log` next to the output (what was recorded where,
  request times, server responses)
- [ ] Publish single-file: `dotnet publish -c Release -r win-x64 --self-contained
  -p:PublishSingleFile=true` → verify .exe runs on a bare machine (no .NET
  install)

## Phase 4 — TUI (Spectre.Console, after console is stable)

- [ ] NuGet: `Spectre.Console`
- [ ] Home screen: server status (`/health` badge ok/busy/unreachable),
    configured URL, command list (`record`, `transcribe`, `meeting`, `devices`,
    `config`)
- [ ] `devices` — table of capture/render devices; pick the mic by arrow keys,
    while all active render devices are captured automatically
- [ ] `record` — live display: elapsed time, file size, per-channel peak meter
    (ASCII bar), stop on a key press (Esc/Space)
- [ ] `meeting` — record → stop → confirm → `transcribe` in one flow
- [ ] `transcribe` — progress bar with elapsed + estimate from FINDINGS.md
    timings (no sub-phase reporting from server; label it elapsed/ETA, not %)
- [ ] Transcript view: rendered in terminal (speaker labels as dim/prompt-style
    prefix, timestamps), then `less`-style pager or save-and-open
- [ ] `config` — edit server URL (with inline `/health` test), model, diarize,
    language, output dir
- [ ] Keep-awake: prevent Windows sleep while recording
    (SetThreadExecutionState)

## Out of scope (for now)

- Live/streaming transcription (server is batch-only)
- Speaker name mapping (SPEAKER_NN → humans) — future LLM pass or manual mapping
- Audio compression (WAV is fine, see FINDINGS.md)
- Multi-server load balancing (single GPU box by design)

## Acceptance test (definition of done for phases 1–3)

Record a real meeting (≥ 30 min) with remote audio on speakers → stop →
transcribe → `transcript.txt` + `transcript.json` on disk with correct speaker
labels and timestamps, total round trip ≈ FINDINGS.md estimate, server `/health`
back to `ok` and scratchpad clean.
