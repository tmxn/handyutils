# STT Server — Findings for the stt-client Application

Verified against real audio (1 h 43 m meeting recording, 48 kHz mono WAV, 9 speakers,
diarization enabled).

## Server

| | |
|---|---|
| Host | `DiaTiPC` (the only machine with the GPU and models) |
| Preferred address | `http://10.11.12.14:8000` — 10 GbE direct link (server listens on `0.0.0.0`) |
| Fallback address | `http://192.168.18.125:8000` — 2.5 GbE LAN |
| Protocol | HTTP, one job at a time (jobs are serialized server-side with a lock) |
| Config | The server base URL is a **user-configurable setting** in the application (env var or config file), not a hard-coded constant. `http://10.11.12.14:8000` is the sensible default (10 GbE); `http://192.168.18.125:8000` is a fallback the user can switch to if the direct link is unavailable. Validate the configured URL with `/health` at startup and surface a clear error (unreachable vs. down vs. busy) rather than assuming.

### Endpoints

**`GET /health`** → `{"status": "ok"}` or `{"status": "busy"}`
Check before sending. `busy` means a job is in flight; queue/wait, don't assume the
server is down.

**`POST /transcribe`** — `multipart/form-data`

| field | type | notes |
|---|---|---|
| `file` | file (required) | any format ffmpeg decodes; WAV is fine and lossless |
| `model` | form field | default `large-v3` |
| `diarize` | form field | default `"true"`; send `"false"` to skip speaker labels |
| `language` | form field | optional, e.g. `en` (skips detection) |

- **Success** → HTTP 200, JSON body (see schema below).
- **Failure** → HTTP 500 with `{"detail": "<whisperx stderr tail>"}`. The detail body
  is the only record of the server-side failure — always read it before giving up.
- Per-request files live in `D:\llama\whisperx\scratchpad` on the server; the server
  cleans them up after each job and sweeps stale entries at startup. The server is
  supervised (auto-restart on crash), console + job logs in `scratchpad\server.log`.

### Response schema (200)

```jsonc
{
  "language": "en",
  "word_segments": [ ... ],           // flat word-level list
  "segments": [
    {
      "start": 0.031,                  // seconds
      "end":   26.573,
      "text":  " looking at the ...",  // leading space
      "avg_logprob": -0.31,
      "speaker": "SPEAKER_08",         // "SPEAKER_00".."SPEAKER_NN"; may be null
      "words": [
        { "word": "looking", "start": 0.031, "end": 0.491, "score": 0.603,
          "speaker": "SPEAKER_08" }
      ]
    }
  ]
}
```

- Segment count ≈ 8 per minute of speech (1149 segments for 103 min).
- Speaker IDs are stable labels, not names (mapping to humans is a client/LLM concern).
- Text has a leading space; strip it.

### Timing & size (measured)

| item | value |
|---|---|
| 48 kHz mono 16-bit WAV | ~5.8 MB/min → **~350 MB/h** of meeting |
| Upload, 600 MB | ~1 min over 10 GbE, ~4 min over 2.5 GbE |
| Transcription + diarization, 1 h 43 m | ~6 min on GPU (large-v3, float16) |
| **Total round trip, 1 h 43 m, 10 GbE** | **~7 min** |

Rule of thumb: expect roughly **1–4 min total per hour of audio** (upload + inference).

## Client behavior rules (learned the hard way)

1. **Long request = normal.** Upload + inference for 1.5 h of audio took minutes of
   pure waiting with no traffic. Use a generous or no client timeout on the POST
   (e.g. ≥ 30 min, or scale with audio duration). Do not retry on silence.
2. **Don't poll the server while a job is in flight.** While transcribing the server
   answers `/health` with `busy` but nothing else; connection-level probes can time
   out and look like "server dead" when it's just working. The reliable completion
   signal is the POST response itself.
3. **Check `/health` before sending.** `ok` → send; `busy` → either wait/poll
   `/health` until `ok`, or just queue (the server itself serializes jobs, so a
   second POST will simply wait — a long idle connection, then a response).
4. **Read the 500 body.** It contains the whisperx stderr and is actionable
   (e.g. "HF token lacks access to a gated model").
5. **Run the upload in the background and watch the output, not the process.**
   Shell process checks (BusyBox `ps`, etc.) can falsely report the client has
   exited; the written transcript file is the ground truth.
6. **Save the raw JSON as well as the rendered transcript** — it has word-level
   timestamps and per-word speaker labels that a plain-text rendering drops.

## Audio format guidance for the recorder

- **WAV (PCM 16-bit) is the right call**: lossless, universally decodable, no
  compression cost client-side, and the server handles it natively. ~350 MB/h is
  trivial for any modern disk. MP3/Opus would save ~15× but buys nothing (the server
  re-decodes to PCM anyway) and adds an encode/decode dependency.
- Record **two channels** (recommended): left = microphone, right = loopback
  (headphone/speaker out). Keeps local and remote sides separable for the LLM
  summarizer and avoids crosstalk being baked into a single mix. 2 ch × 48 kHz ×
  16 bit ≈ 700 MB/h — still fine. If simpler, a pre-mixed mono track also works
  (the test file was 48 kHz mono).
- 44.1/48 kHz are both fine; the model internally downsamples to 16 kHz.
- Keep it a single file for the whole meeting — one upload, one request, one response.
