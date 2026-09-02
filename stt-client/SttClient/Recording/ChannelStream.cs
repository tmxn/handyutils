using System.Collections.Concurrent;
using NAudio.Wave;

namespace SttClient.Recording;

/// <summary>
/// One capture stream: converts raw WASAPI bytes (any supported PCM/float format,
/// any channel count, native rate) to mono 32-bit float at the target rate,
/// and holds the converted samples in a queue for the writer to drain.
/// </summary>
public sealed class ChannelStream
{
    private readonly ConcurrentQueue<float> _samples = new();
    private readonly LinearResampler? _resampler;
    private readonly float[] _monoWork = new float[8192];
    private volatile float _peak;

    public WaveFormat Format { get; }
    public int InputChannels => Format.Channels;
    public int InputRate => Format.SampleRate;
    public int QueuedSamples => _samples.Count;

    public ChannelStream(WaveFormat format, int targetRate)
    {
        Format = format;
        if (format.Encoding != WaveFormatEncoding.Pcm &&
            !(format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32))
            throw new NotSupportedException(
                $"Unsupported capture format: {format.Encoding} {format.BitsPerSample}-bit");
        _resampler = format.SampleRate == targetRate
            ? null
            : new LinearResampler(format.SampleRate, targetRate);
    }

    /// <summary>Feeds raw bytes from a DataAvailable event (thread-safe).</summary>
    public void Add(byte[] buffer, int bytes)
    {
        int frames = ConvertToMono(buffer, bytes, _monoWork);
        for (int i = 0; i < frames; i++)
        {
            _peak = Math.Max(_peak, Math.Abs(_monoWork[i]));
            _samples.Enqueue(_monoWork[i]);
        }
        if (_resampler == null) return;

        int produced;
        do
        {
            produced = _resampler.Process(_monoWork, _monoWork);
            // NOTE: Process() output aliases the input buffer here, which is safe
            // because interpolation reads input[idx], input[idx+1] and writes
            // output[n] with n < produced input frames consumed.
            for (int i = 0; i < produced; i++)
            {
                _peak = Math.Max(_peak, Math.Abs(_monoWork[i]));
                _samples.Enqueue(_monoWork[i]);
            }
        } while (produced == _monoWork.Length);
    }

    /// <summary>
    /// Drains up to <paramref name="count"/> samples into <paramref name="dest"/>,
    /// padding with silence if the stream has underrun. Returns peak since the
    /// last call (for level meters).
    /// </summary>
    public float Drain(int count, float[] dest)
    {
        float peak = _peak; _peak = 0f;
        for (int i = 0; i < count; i++)
        {
            if (_samples.TryDequeue(out float s)) dest[i] = s;
            else dest[i] = 0f;
        }
        return peak;
    }

    /// <summary>
    /// Converts raw capture bytes to mono float in <paramref name="dest"/> and
    /// returns the frame count. Averages all channels down to one.
    /// </summary>
    private int ConvertToMono(byte[] buffer, int bytes, float[] dest)
    {
        var fmt = Format;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int frames = bytes / (4 * fmt.Channels);
            for (int f = 0; f < frames && f < dest.Length; f++)
            {
                double sum = 0;
                for (int c = 0; c < fmt.Channels; c++)
                    sum += BitConverter.ToSingle(buffer, (f * fmt.Channels + c) * 4);
                dest[f] = (float)(sum / fmt.Channels);
            }
            return frames;
        }

        // PCM
        int bps = fmt.BitsPerSample / 8;
        int n = bytes / (bps * fmt.Channels);
        for (int f = 0; f < n && f < dest.Length; f++)
        {
            double sum = 0;
            for (int c = 0; c < fmt.Channels; c++)
            {
                int off = (f * fmt.Channels + c) * bps;
                double v = fmt.BitsPerSample switch
                {
                    8 => (sbyte)buffer[off] / 128.0,
                    16 => BitConverter.ToInt16(buffer, off) / 32768.0,
                    // 24-bit little-endian, sign-extended
                    24 => (buffer[off] | (buffer[off + 1] << 8) | ((sbyte)buffer[off + 2] << 16)) / 8388608.0,
                    32 => BitConverter.ToInt32(buffer, off) / 2147483648.0,
                    _ => throw new NotSupportedException($"Unsupported bit depth {fmt.BitsPerSample}")
                };
                sum += v;
            }
            dest[f] = (float)(sum / fmt.Channels);
        }
        return n;
    }
}
