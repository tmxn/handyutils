using NAudio.CoreAudioApi;
using NAudio.Wave;
using SttClient.Config;

namespace SttClient.Recording;

/// <summary>
/// Records the microphone (L) and a render device via WASAPI loopback (R) into
/// one interleaved 2-channel 16-bit PCM WAV. Underruns on either stream are
/// padded with silence; a device dying mid-recording is reported, and the other
/// channel keeps going.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly MMDevice _micDevice;
    private readonly MMDevice _loopbackDevice;
    private readonly string _path;
    private readonly int _sampleRate;
    private readonly bool _keepAlive;

    private WaveFileWriter? _writer;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loopback;
    private WasapiOut? _keepAliveOut;
    private ChannelStream? _left;   // mic
    private ChannelStream? _right;  // loopback
    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private volatile bool _micFailed, _loopbackFailed;
    private long _samplesWritten;   // per channel

    public string Path => _path;
    public int SampleRate => _sampleRate;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samplesWritten / _sampleRate);
    public bool MicFailed => _micFailed;
    public bool LoopbackFailed => _loopbackFailed;

    public Recorder(MMDevice micDevice, MMDevice loopbackDevice, string path,
        int sampleRate = 48000, bool keepAlive = true)
    {
        _micDevice = micDevice;
        _loopbackDevice = loopbackDevice;
        _path = path;
        _sampleRate = sampleRate;
        _keepAlive = keepAlive;
    }

    public void Start()
    {
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

        _loopback = new WasapiLoopbackCapture(_loopbackDevice);
        _right = new ChannelStream(_loopback.WaveFormat, _sampleRate);
        _loopback.DataAvailable += LoopbackData;
        _loopback.RecordingStopped += (s, e) =>
        {
            if (e.Exception != null)
            {
                _loopbackFailed = true;
                Console.Error.WriteLine($"[warn] loopback stream stopped: {e.Exception.Message} — continuing on mic only");
            }
        };

        // WASAPI loopback quirk: if nothing is playing, no loopback data arrives.
        // Play a silent stream on the render device to keep the mix alive.
        if (_keepAlive)
        {
            try
            {
                _keepAliveOut = new WasapiOut(_loopbackDevice, AudioClientShareMode.Shared, true, 200);
                _keepAliveOut.Init(new SilenceProvider(_loopback.WaveFormat));
                _keepAliveOut.Play();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] silence keep-alive failed ({ex.Message}); " +
                    "loopback may be silent while no audio is playing");
                _keepAliveOut = null;
            }
        }

        _writerTask = Task.Run(() => WriterLoop(_cts.Token));
        _loopback.StartRecording();
        _mic.StartRecording();
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

    private void LoopbackData(object? sender, WaveInEventArgs e)
    {
        try { _right!.Add(e.Buffer, e.BytesRecorded); }
        catch (Exception ex)
        {
            _loopbackFailed = true;
            Console.Error.WriteLine($"[warn] loopback stream error: {ex.Message} — continuing with silence");
        }
    }

    private void WriterLoop(CancellationToken ct)
    {
        const int periodMs = 10;
        int expected = _sampleRate * periodMs / 1000;
        var left = new float[expected];
        var right = new float[expected];
        var pcm = new byte[expected * 4];
        float leftPeak = 0, rightPeak = 0;
        var lastMeter = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            leftPeak = _left!.Drain(expected, left);
            rightPeak = _right!.Drain(expected, right);
            for (int i = 0; i < expected; i++)
            {
                pcm[i * 4] = (byte)(short)Math.Clamp(left[i] * 32767f, -32768, 32767);
                pcm[i * 4 + 1] = (byte)((short)Math.Clamp(left[i] * 32767f, -32768, 32767) >> 8);
                pcm[i * 4 + 2] = (byte)(short)Math.Clamp(right[i] * 32767f, -32768, 32767);
                pcm[i * 4 + 3] = (byte)((short)Math.Clamp(right[i] * 32767f, -32768, 32767) >> 8);
            }
            _writer!.Write(pcm, 0, pcm.Length);
            _samplesWritten += expected;

            if (Environment.TickCount64 - lastMeter >= 500)
            {
                lastMeter = Environment.TickCount64;
                OnMeter?.Invoke(_samplesWritten, leftPeak, rightPeak);
            }
            Thread.Sleep(periodMs);
        }
    }

    /// <summary>Raised ~2×/sec with total samples written and per-channel peaks (0..1).</summary>
    public event Action<long, float, float>? OnMeter;

    public void Stop()
    {
        if (_cts == null) return;
        try { _mic?.StopRecording(); } catch { }
        try { _loopback?.StopRecording(); } catch { }
        _cts.Cancel();
        try { _writerTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        try
        {
            if (_keepAliveOut != null) { _keepAliveOut.Stop(); _keepAliveOut.Dispose(); }
        }
        catch { }
        try { _writer?.Dispose(); } catch { } // Dispose flushes the WAV header
        try { _mic?.Dispose(); } catch { }
        try { _loopback?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _micDevice.Dispose();
        _loopbackDevice.Dispose();
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
