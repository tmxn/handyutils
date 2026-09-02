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
            ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : ByIndex(en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active), index, "capture");
    }

    public static MMDevice GetRender(int? index)
    {
        var en = new MMDeviceEnumerator();
        return index is null
            ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications)
            : ByIndex(en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active), index, "render");
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
            defaultId = en.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch { /* no default for this flow */ }

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
            result.Add(new DeviceInfo(i++, d.FriendlyName, d.ID == defaultId, format));
        }
        return result;
    }
}
