using System.IO;
using System.Text.Json;

namespace WorkTracker.Services;

/// <summary>Owns the runtime data directory (%USERPROFILE%\WorkTrackerData) and config.json.</summary>
public sealed class ConfigStore
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WorkTrackerData");

    public static string ScoresPath => Path.Combine(DataDir, "scores.json");
    public static string ReportsDir => Path.Combine(DataDir, "reports");
    public static string RawDir => Path.Combine(DataDir, "raw");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ConfigPath { get; }

    public ConfigStore()
    {
        ConfigPath = Path.Combine(DataDir, "config.json");
    }

    public static void BootstrapDataDir()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(RawDir);
    }

    public AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg != null) return Normalize(cfg);
            }
            catch
            {
                // Fall through: start with defaults, keep the broken file around for inspection.
            }
        }
        var defaults = DefaultConfig();
        Save(defaults);
        return defaults;
    }

    public void Save(AppConfig cfg)
    {
        BootstrapDataDir();
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    private static AppConfig Normalize(AppConfig cfg)
    {
        cfg.Llm ??= new LlmSettings();
        if (cfg.Llm.Args == null) cfg.Llm.Args = new List<string>();
        if (cfg.Llm.TimeoutSeconds <= 0) cfg.Llm.TimeoutSeconds = 600;
        cfg.Grid ??= new GridSettings();
        if (cfg.Grid.LoadThresholds == null || cfg.Grid.LoadThresholds.Count != 5)
            cfg.Grid.LoadThresholds = new List<int> { 0, 1, 10, 20, 35 };
        if (string.IsNullOrWhiteSpace(cfg.Theme))
            cfg.Theme = "auto";
        if (string.IsNullOrWhiteSpace(cfg.Llm.ThinkingEffort))
            cfg.Llm.ThinkingEffort = "medium";
        cfg.Developers ??= new List<Developer>();
        foreach (var d in cfg.Developers)
        {
            d.AuthorNames ??= new List<string>();
            d.AuthorEmails ??= new List<string>();
            if (string.IsNullOrEmpty(d.Id)) d.Id = slug(d.DisplayName) ?? "dev";
        }
        return cfg;
    }

    private static AppConfig DefaultConfig()
    {
        // Reasonable default: the repo this source tree lives in (WorkTrackerData is
        // per-user, so on first run we can't know the target repo — point at the
        // current working directory and let the user fix it in Settings).
        return new AppConfig
        {
            RepoPath = Environment.CurrentDirectory,
            Llm = new LlmSettings(),
            Grid = new GridSettings(),
        };
    }

    internal static string? slug(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? null : r;
    }
}
