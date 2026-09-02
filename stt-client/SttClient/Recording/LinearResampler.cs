namespace SttClient.Recording;

/// <summary>
/// Stateful linear-interpolation resampler. Good enough for the small rate
/// differences WASAPI throws at us (44100 vs 48000 Hz).
/// </summary>
public sealed class LinearResampler
{
    private readonly double _step; // input samples consumed per output sample
    private double _pos;           // input position of the next output sample

    public LinearResampler(int inRate, int outRate)
    {
        _step = (double)inRate / outRate;
    }

    /// <summary>Feeds one chunk; returns however many output samples could be produced.</summary>
    public int Process(float[] input, float[] output)
    {
        int n = 0;
        while (n < output.Length)
        {
            int idx = (int)_pos;
            if (idx + 1 >= input.Length) break; // need one more input sample
            double frac = _pos - idx;
            output[n] = (float)(input[idx] * (1.0 - frac) + input[idx + 1] * frac);
            _pos += _step;
            n++;
        }
        return n;
    }
}
