namespace SttClient.Recording;

/// <summary>
/// Display-only response curve for the level meters. Audio written to disk is
/// never changed; this makes quiet speech easier to see without turning the
/// meter into an on/off indicator.
/// </summary>
public static class LevelMeter
{
    // A gentle compression: quiet speech is more visible, while loudness still
    // occupies a continuous range and reaches full scale normally.
    private const float DisplayExponent = 0.60f;

    public static float ToDisplay(float peak) =>
        MathF.Pow(Math.Clamp(peak, 0f, 1f), DisplayExponent);
}
