using System.IO;
using System.Text.Json;

namespace WorkTracker.Services;

/// <summary>Reads/writes scores.json and reports/.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // ---------- scores.json ----------

    public static ScoreCache LoadScores()
    {
        if (!File.Exists(ConfigStore.ScoresPath))
            return new ScoreCache();
        try
        {
            return JsonSerializer.Deserialize<ScoreCache>(File.ReadAllText(ConfigStore.ScoresPath), JsonOpts)
                   ?? new ScoreCache();
        }
        catch
        {
            // Corrupt cache: preserve it so nothing is lost, start clean.
            try { File.Copy(ConfigStore.ScoresPath, ConfigStore.ScoresPath + ".corrupt", true); } catch { }
            return new ScoreCache();
        }
    }

    public static void SaveScores(ScoreCache cache)
    {
        Directory.CreateDirectory(ConfigStore.DataDir);
        File.WriteAllText(ConfigStore.ScoresPath, JsonSerializer.Serialize(cache, JsonOpts));
    }

    // ---------- reports ----------

    public static string ReportPath(string developerId, string weekStart)
        => Path.Combine(ConfigStore.ReportsDir, developerId, weekStart + ".json");

    public static ReportFile? LoadReport(string developerId, string weekStart)
    {
        var path = ReportPath(developerId, weekStart);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReportFile>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveReport(ReportFile report)
    {
        var path = ReportPath(report.Developer, report.WeekStart);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOpts));
    }
}

/// <summary>Extracts the first balanced JSON object from an LLM response, tolerating markdown fences.</summary>
internal static class JsonExtract
{
    public static string? ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Strip markdown fences if present (also helps find the first '{').
        var start = text.IndexOf('{');
        while (start >= 0)
        {
            var (json, ok) = TakeBalanced(text, start);
            if (ok && json != null) return json;
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    private static (string? json, bool balanced) TakeBalanced(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
            }
            else
            {
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return (text[start..(i + 1)], true);
                }
            }
        }
        return (null, false);
    }
}
