using System.Net.Http.Headers;
using System.Text.Json;

namespace SttClient.Stt;

public enum Health { Ok, Busy, Unreachable }

/// <summary>
/// Thin wrapper around the WhisperX server. Rules from FINDINGS.md:
/// - check /health before sending (ok | busy | unreachable);
/// - the POST is a single long blocking request (no polling, no mid-job probes);
/// - timeouts are generous and scale with audio duration;
/// - on HTTP 500 the JSON body ("detail") is the only record of the failure —
///   always read it.
/// </summary>
public sealed class SttServer : IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public SttServer(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // the POST runs for minutes on purpose; each call carries its own cancellation
    }

    public async Task<Health> GetHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await _http.GetAsync($"{BaseUrl}/health", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Health.Unreachable;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            return status == "busy" ? Health.Busy : Health.Ok;
        }
        catch
        {
            // health check is best-effort: timeouts, DNS, and bad bodies all mean "unreachable"
            return Health.Unreachable;
        }
    }

    /// <summary>Reads the audio duration from the WAV header (0 if unknown).</summary>
    public static TimeSpan WavDuration(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (new string(br.ReadChars(4)) != "RIFF") return TimeSpan.Zero;
            br.ReadUInt32(); // RIFF size
            if (new string(br.ReadChars(4)) != "WAVE") return TimeSpan.Zero;
            long channels = 0, rate = 0, bits = 0;
            while (fs.Position + 8 <= fs.Length)
            {
                var name = new string(br.ReadChars(4));
                long size = br.ReadUInt32();
                if (name == "fmt ")
                {
                    br.ReadUInt16(); // audio format
                    channels = br.ReadUInt16();
                    rate = br.ReadUInt32();
                    br.ReadUInt32(); // bytes per second
                    br.ReadUInt16(); // block align
                    bits = br.ReadUInt16();
                    fs.Position += Math.Max(0, size - 16);
                }
                else if (name == "data")
                {
                    if (channels == 0 || rate == 0 || bits == 0) return TimeSpan.Zero;
                    return TimeSpan.FromSeconds((double)size / (rate * channels * (bits / 8.0)));
                }
                else
                {
                    fs.Position += size;
                }
                fs.Position += size & 1; // 16-bit word alignment padding
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[wav-dur] {ex}"); }
        return TimeSpan.Zero;
    }

    public sealed record Progress(
        string Phase,             // "upload" | "server"
        long BytesDone, long BytesTotal,
        TimeSpan Elapsed,
        TimeSpan EstimatedTotal);

    public async Task<string> TranscribeAsync(
        string filePath,
        string model,
        bool diarize,
        string? language,
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        var duration = WavDuration(filePath);
        var fileSize = new FileInfo(filePath).Length;
        // Rule of thumb from FINDINGS.md: ~7 min per hour of audio over 10 GbE.
        // Be generous: 20 min base + 25 min per hour of audio.
        var timeout = TimeSpan.FromMinutes(20 + duration.TotalHours * 25);
        var estimatedTotal = TimeSpan.FromSeconds(duration.TotalHours * 7 * 60);
        var started = Environment.TickCount64;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var form = new MultipartFormDataContent();
        var stream = new CountingStream(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read));
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(diarize ? "true" : "false"), "diarize");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        // With ResponseHeadersRead the response arrives after the full upload
        // (the server buffers the file), so report upload bytes until it lands.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/transcribe") { Content = form };
        var postTask = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        while (!postTask.IsCompleted)
        {
            try { await Task.Delay(500, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (!postTask.IsCompleted)
            {
                // Once every byte is out the door the connection just waits on the
                // server (headers arrive only after the job finishes).
                var phase = stream.BytesRead >= fileSize ? "server" : "upload";
                progress?.Report(new Progress(phase, stream.BytesRead, fileSize,
                    TimeSpan.FromTicks(Environment.TickCount64 - started), estimatedTotal));
            }
        }
        using var resp = await postTask.ConfigureAwait(false);

        // Now the server is working; the connection sits idle for minutes.
        // Never probe /health mid-job — the POST response is the only reliable
        // completion signal (FINDINGS.md rule 2).
        var bodyTask = resp.Content.ReadAsStringAsync();
        while (!bodyTask.IsCompleted)
        {
            await Task.WhenAny(bodyTask, Task.Delay(500, cts.Token)).ConfigureAwait(false);
            if (!bodyTask.IsCompleted)
                progress?.Report(new Progress("server", fileSize, fileSize,
                    TimeSpan.FromTicks(Environment.TickCount64 - started), estimatedTotal));
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await bodyTask.ConfigureAwait(false);
            // 500 bodies carry the whisperx stderr tail in "detail" — print verbatim.
            throw new SttServerError((int)resp.StatusCode, body);
        }
        return await bodyTask.ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesRead { get; private set; }
        public CountingStream(Stream inner) => _inner = inner;
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
    }
}

public sealed class SttServerError : Exception
{
    public int StatusCode { get; }
    public string Body { get; }
    public SttServerError(int statusCode, string body)
        : base($"Server returned HTTP {statusCode}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}
