using NAudio.CoreAudioApi;

namespace SttClient.Recording;

public sealed record DeviceInfo(int Index, string Name, bool IsDefault, string Format)
{
    public override string ToString() =>
        $"{Index,3}  {(IsDefault ? "*" : " ")}  {Name}  [{Format}]";
}

/// <summary>Enumeration of capture (mic) and render (loopback source) devices.</summary>
public static class AudioDevices
{
    public static List<DeviceInfo> ListCaptures() =>
        Enumerate(DataFlow.Capture);

    public static List<DeviceInfo> ListRenders() =>
        Enumerate(DataFlow.Render);

    public static MMDevice GetCapture(int? index)
    {
        var en = new MMDeviceEnumerator();
        return index is null
            ? DefaultEndpoint(en, DataFlow.Capture)
            : ByIndex(en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active), index, "capture");
    }

    /// <summary>Returns every active render endpoint for WASAPI loopback capture.</summary>
    public static List<MMDevice> GetRenders() =>
        new MMDeviceEnumerator()
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();

    /// <summary>
    /// The user's regular "Default Device" (Role.Console — what Windows shows with a
    /// speaker icon), NOT the "Default Communication Device" (Role.Communications),
    /// which can silently point at e.g. a headset while the default is the monitor.
    /// </summary>
    private static MMDevice DefaultEndpoint(MMDeviceEnumerator en, DataFlow flow)
    {
        try { return en.GetDefaultAudioEndpoint(flow, Role.Console); }
        catch
        {
            // fall back to the communications default if the console default is missing
            return en.GetDefaultAudioEndpoint(flow, Role.Communications);
        }
    }

    private static MMDevice ByIndex(IEnumerable<MMDevice> devices, int? index, string kind)
    {
        var list = devices.ToList();
        if (index is null || index < 0 || index >= list.Count)
            throw new ArgumentException(
                $"No {kind} device at index {index} (have {list.Count}). Run 'stt-client devices'.");
        return list[index.Value];
    }

    private static List<DeviceInfo> Enumerate(DataFlow flow)
    {
        var en = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            defaultId = en.GetDefaultAudioEndpoint(flow, Role.Console).ID;
        }
        catch
        {
            try { defaultId = en.GetDefaultAudioEndpoint(flow, Role.Communications).ID; }
            catch { /* no default for this flow */ }
        }

        var result = new List<DeviceInfo>();
        var i = 0;
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            string format = "?";
            try
            {
                var mix = d.AudioClient.MixFormat;
                format = $"{mix.SampleRate} Hz, {mix.BitsPerSample}-bit, {mix.Channels} ch";
            }
            catch { }
            // Capture volume/mute is applied by the audio engine before apps see samples —
            // a muted mic records silence, so make the state visible.
            try
            {
                var vol = d.AudioEndpointVolume;
                format += $", vol {vol.MasterVolumeLevelScalar:P0}";
                if (vol.Mute) format += ", MUTED";
            }
            catch { }
            result.Add(new DeviceInfo(i++, d.FriendlyName, d.ID == defaultId, format));
        }
        return result;
    }
}
