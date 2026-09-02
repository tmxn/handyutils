using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SttClient.Stt;

public sealed class Transcript
{
    public string? Language { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public List<Word> WordSegments { get; set; } = new();

    [JsonIgnore]
    public TimeSpan TotalDuration
    {
        get
        {
            var max = 0.0;
            foreach (var s in Segments) max = Math.Max(max, s.End);
            return TimeSpan.FromSeconds(max);
        }
    }
}

public sealed class Segment
{
    public float Start { get; set; }
    public float End { get; set; }
    public string Text { get; set; } = "";
    public float AvgLogprob { get; set; }
    public string? Speaker { get; set; }
    public List<Word>? Words { get; set; }
}

public sealed class Word
{
    [JsonPropertyName("word")]
    public string Text { get; set; } = "";
    public float Start { get; set; }
    public float End { get; set; }
    public float? Score { get; set; }
    public string? Speaker { get; set; }
}

public static class TranscriptRenderer
{
    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Transcript Parse(string json) =>
        JsonSerializer.Deserialize<Transcript>(json, ParseOpts)
        ?? throw new InvalidOperationException("Empty transcript JSON");

    /// <summary>
    /// Human-readable rendering:
    ///   [00:12:34.567] SPEAKER_03: some words...
    /// Leading spaces in segment text are stripped (server quirk).
    /// </summary>
    public static string RenderText(Transcript t)
    {
        var sb = new StringBuilder();
        foreach (var s in t.Segments)
        {
            sb.Append('[').Append(Fmt(s.Start)).Append("] ");
            sb.Append(s.Speaker ?? "      ??").Append(": ");
            sb.Append(s.Text.TrimStart(' '));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Pretty-prints the raw JSON (word-level data preserved as-is).</summary>
    public static string PrettyJson(string json) =>
        JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(json),
            new JsonSerializerOptions { WriteIndented = true });

    public static string Fmt(float seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{(int)(ts.TotalMilliseconds % 1000):000}";
    }
}
