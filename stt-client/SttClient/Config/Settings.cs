using System.Text.Json;
using System.Text.Json.Serialization;

namespace SttClient.Config;

/// <summary>
/// Persistent settings, stored in <c>~/.stt-client/stt-client.json</c>.
/// </summary>
public sealed class Settings
{
    public const string FileName = "stt-client.json";

    public static string DataDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = AppContext.BaseDirectory;
            return Path.Combine(home, ".stt-client");
        }
    }

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
    /// <summary>Directory for recordings, transcripts, and logs.</summary>
    public string OutputDir { get; set; } = DataDirectory;

    /// <summary>Absolute output path; relative configured paths live under DataDirectory.</summary>
    [JsonIgnore]
    public string ResolvedOutputDir
    {
        get
        {
            var dir = string.IsNullOrWhiteSpace(OutputDir) ? DataDirectory : OutputDir.Trim();
            if (dir == "~") return DataDirectory;
            if (dir.StartsWith("~/", StringComparison.Ordinal) || dir.StartsWith("~\\", StringComparison.Ordinal))
                dir = dir[2..];
            return Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(DataDirectory, dir));
        }
    }

    /// <summary>Resolves a generated or user-supplied output path.</summary>
    public string ResolveOutputPath(string path)
    {
        var value = path.Trim();
        if (value == "~") return ResolvedOutputDir;
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = value[2..];
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(ResolvedOutputDir, value));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static Settings Load()
    {
        try
        {
            var path = Path.Combine(DataDirectory, FileName);
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
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
