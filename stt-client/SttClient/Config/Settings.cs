using System.Text.Json;
using System.Text.Json.Serialization;

namespace SttClient.Config;

/// <summary>
/// Persistent settings, stored in <c>stt-client.json</c> in the working directory.
/// </summary>
public sealed class Settings
{
    public const string FileName = "stt-client.json";

    public string ServerUrl { get; set; } = "http://10.11.12.14:8000";
    public string Model { get; set; } = "large-v3";
    public bool Diarize { get; set; } = true;
    /// <summary>Optional fixed language, e.g. "en". Null = let the model detect.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }
    /// <summary>Capture device index (mic). Null = system default.</summary>
    public int? MicDevice { get; set; }
    // Loopback is intentionally not configurable: recording follows all active
    // output endpoints, so switching headphones during a meeting is automatic.
    public string OutputDir { get; set; } = ".";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static Settings Load()
    {
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOpts);
                if (s != null) return s;
            }
        }
        catch
        {
            // fall through to defaults; a corrupt config should not kill the app
        }
        return new Settings();
    }

    public void Save()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
