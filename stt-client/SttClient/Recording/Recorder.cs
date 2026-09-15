using NAudio.CoreAudioApi;
using NAudio.Wave;
using SttClient.Config;

namespace SttClient.Recording;

/// <summary>
/// Records the microphone (L) and every active render device via WASAPI loopback
/// (R) into one interleaved 2-channel 16-bit PCM WAV. Render streams are mixed
/// into the right channel, with underruns padded with silence; a device dying
/// mid-recording is reported, and the other streams keep going.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly MMDevice _micDevice;
    private readonly IReadOnlyList<MMDevice> _loopbackDevices;
    private readonly string _path;
    private readonly int _sampleRate;
    private readonly bool _keepAlive;

    private readonly List<LoopbackStream> _loopbacks = new();
    private WaveFileWriter? _writer;
    private WasapiCapture? _mic;
    private ChannelStream? _left;   // mic
    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private volatile bool _micFailed, _loopbackFailed;
    private long _samplesWritten;   // per channel

    public string Path => _path;
    public int SampleRate => _sampleRate;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samplesWritten / _sampleRate);
    public bool MicFailed => _micFailed;
    public bool LoopbackFailed => _loopbackFailed;
    public int LoopbackDeviceCount => _loopbackDevices.Count;

    public Recorder(MMDevice micDevice, IReadOnlyList<MMDevice> loopbackDevices, string path,
        int sampleRate = 48000, bool keepAlive = true)
    {
        _micDevice = micDevice;
        _loopbackDevices = loopbackDevices.ToList();
        _path = path;
        _sampleRate = sampleRate;
        _keepAlive = keepAlive;
    }

    public void Start()
    {
        if (_loopbackDevices.Count == 0)
            throw new InvalidOperationException("No active audio output devices were found.");

        _writer = new WaveFileWriter(_path, new WaveFormat(_sampleRate, 16, 2));
        _cts = new CancellationTokenSource();

        _mic = new WasapiCapture(_micDevice);
        _left = new ChannelStream(_mic.WaveFormat, _sampleRate);
        _mic.DataAvailable += MicData;
        _mic.RecordingStopped += (s, e) =>
        {
            if (e.Exception != null)
            {
                _micFailed = true;
                Console.Error.WriteLine($"[warn] mic stream stopped: {e.Exception.Message} — continuing on loopback only");
            }
        };

        foreach (var device in _loopbackDevices)
        {
            WasapiLoopbackCapture? capture = null;
            try
            {
                capture = new WasapiLoopbackCapture(device);
                var state = new LoopbackStream(device, capture, new ChannelStream(capture.WaveFormat, _sampleRate));
                capture.DataAvailable += (s, e) => LoopbackData(state, e);
                capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                    {
                        state.Failed = true;
                        _loopbackFailed = true;
                        Console.Error.WriteLine($"[warn] loopback '{device.FriendlyName}' stopped: " +
                            $"{e.Exception.Message} — continuing on other outputs");
                    }
                };

                // WASAPI loopback quirk: if nothing is playing, no loopback data arrives.
                // Play a silent stream on each render device to keep every mix alive.
                if (_keepAlive)
                {
                    try
                    {
                        state.KeepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                        state.KeepAlive.Init(new SilenceProvider(capture.WaveFormat));
                        state.KeepAlive.Play();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[warn] silence keep-alive failed for '{device.FriendlyName}' " +
                            $"({ex.Message}); loopback may be silent while no audio is playing");
                        state.KeepAlive = null;
                    }
                }

                _loopbacks.Add(state);
            }
            catch (Exception ex)
            {
                try { capture?.Dispose(); } catch { }
                _loopbackFailed = true;
                Console.Error.WriteLine($"[warn] could not open loopback '{device.FriendlyName}': {ex.Message}");
            }
        }

        if (_loopbacks.Count == 0)
            throw new InvalidOperationException("Could not open any audio output device for loopback capture.");

        _writerTask = Task.Run(() => WriterLoop(_cts.Token));

        try { _mic.StartRecording(); }
        catch
        {
            _micFailed = true;
            throw;
        }

        foreach (var state in _loopbacks)
        {
            try { state.Capture.StartRecording(); }
            catch (Exception ex)
            {
                state.Failed = true;
                _loopbackFailed = true;
                Console.Error.WriteLine($"[warn] could not start loopback '{state.Device.FriendlyName}': " +
                    $"{ex.Message} — continuing on other outputs");
            }
        }

        if (_loopbacks.All(s => s.Failed))
            throw new InvalidOperationException("Could not start loopback capture on any audio output device.");
    }

    private void MicData(object? sender, WaveInEventArgs e)
    {
        try { _left!.Add(e.Buffer, e.BytesRecorded); }
        catch (Exception ex)
        {
            _micFailed = true;
            Console.Error.WriteLine($"[warn] mic stream error: {ex.Message} — continuing with silence");
        }
    }

    private void LoopbackData(LoopbackStream state, WaveInEventArgs e)
    {
        try { state.Channel.Add(e.Buffer, e.BytesRecorded); }
        catch (Exception ex)
        {
            state.Failed = true;
            _loopbackFailed = true;
            Console.Error.WriteLine($"[warn] loopback '{state.Device.FriendlyName}' stream error: " +
                $"{ex.Message} — continuing with other outputs");
        }
    }

    private void WriterLoop(CancellationToken ct)
    {
        // Buffers sized for ~1 s of audio: enough to drain fast when the queue
        // has built up, so the writer never falls behind the realtime feed.
        int max = _sampleRate;
        var left = new float[max];
        var right = new float[max];
        var source = new float[max];
        var pcm = new byte[max * 4];
        float leftPeak = 0, rightPeak = 0;
        var lastMeter = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            // All channels run at the target rate, so take the deepest queue.
            int n = 0;
            if (_left != null) n = Math.Max(n, _left.QueuedSamples);
            foreach (var state in _loopbacks)
                n = Math.Max(n, state.Channel.QueuedSamples);
            if (n == 0)
            {
                Thread.Sleep(5);
                continue;
            }
            n = Math.Min(n, max);

            leftPeak = _left!.Drain(n, left);
            Array.Clear(right, 0, n);
            foreach (var state in _loopbacks)
            {
                state.Channel.Drain(n, source);
                for (int i = 0; i < n; i++)
                    right[i] += source[i];
            }

            // Each endpoint is captured so recording follows whichever output is
            // in use. The expected setup has one endpoint carrying audio at a time,
            // so sum the streams without attenuating the active one.
            rightPeak = 0;
            for (int i = 0; i < n; i++)
            {
                right[i] = Math.Clamp(right[i], -1f, 1f);
                rightPeak = Math.Max(rightPeak, Math.Abs(right[i]));

                short l = (short)Math.Clamp(left[i] * 32767f, -32768, 32767);
                short r = (short)Math.Clamp(right[i] * 32767f, -32768, 32767);
                pcm[i * 4] = (byte)l;
                pcm[i * 4 + 1] = (byte)(l >> 8);
                pcm[i * 4 + 2] = (byte)r;
                pcm[i * 4 + 3] = (byte)(r >> 8);
            }
            _writer!.Write(pcm, 0, n * 4);
            _samplesWritten += n;

            if (Environment.TickCount64 - lastMeter >= 500)
            {
                lastMeter = Environment.TickCount64;
                OnMeter?.Invoke(_samplesWritten, leftPeak, rightPeak);
            }

            // Pace only when caught up; when the queue has a backlog (loop
            // overhead, brief stalls) run flat-out so nothing accumulates.
            int queued = _left.QueuedSamples;
            foreach (var state in _loopbacks)
                queued += state.Channel.QueuedSamples;
            if (queued <= _sampleRate / 10)
                Thread.Sleep(5);
        }
    }

    private int TotalQueued()
    {
        int queued = 0;
        if (_left != null) queued += _left.QueuedSamples;
        foreach (var state in _loopbacks)
            queued += state.Channel.QueuedSamples;
        return queued;
    }

    /// <summary>Raised ~2×/sec with total samples written and per-channel peaks (0..1).</summary>
    public event Action<long, float, float>? OnMeter;

    public void Stop()
    {
        if (_cts == null) return;
        try { _mic?.StopRecording(); } catch { }
        foreach (var state in _loopbacks)
        {
            try { state.Capture.StopRecording(); } catch { }
        }

        // StopRecording() joins each capture thread, so all DataAvailable
        // callbacks have fired by now — the queues hold the final data.
        // Give the writer a bounded chance to drain whatever remains (it runs
        // unpaced when behind) before cancelling, so no tail is dropped.
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && TotalQueued() > 0)
            Thread.Sleep(10);

        _cts.Cancel();
        try { _writerTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }

        foreach (var state in _loopbacks)
        {
            try { state.KeepAlive?.Stop(); } catch { }
            try { state.KeepAlive?.Dispose(); } catch { }
        }
        try { _writer?.Dispose(); } catch { } // Dispose flushes the WAV header
        try { _mic?.Dispose(); } catch { }
        foreach (var state in _loopbacks)
        {
            try { state.Capture.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _micDevice.Dispose();
        foreach (var device in _loopbackDevices)
            device.Dispose();
    }

    private sealed class LoopbackStream
    {
        public MMDevice Device { get; }
        public WasapiLoopbackCapture Capture { get; }
        public ChannelStream Channel { get; }
        public WasapiOut? KeepAlive { get; set; }
        public volatile bool Failed;

        public LoopbackStream(MMDevice device, WasapiLoopbackCapture capture, ChannelStream channel)
        {
            Device = device;
            Capture = capture;
            Channel = channel;
        }
    }

    /// <summary>Zero-filled provider of the device mix format (keeps loopback alive).</summary>
    private sealed class SilenceProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; }
        private readonly byte[] _zero = new byte[4096];
        public SilenceProvider(WaveFormat fmt) => WaveFormat = fmt;
        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(_zero, 0, _zero.Length);
            int written = 0;
            while (written < count)
            {
                int n = Math.Min(count - written, _zero.Length);
                Buffer.BlockCopy(_zero, 0, buffer, offset + written, n);
                written += n;
            }
            return written;
        }
    }
}
