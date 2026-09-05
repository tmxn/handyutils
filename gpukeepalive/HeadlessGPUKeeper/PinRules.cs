using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadlessGpuKeeper;

/// <summary>
/// One tracked application whose executable lives at a version-stamped path.
///
/// Such apps cannot be pinned durably: they are not MSIX (so they have no AUMID) and
/// their install folder carries the version, so a UserGpuPreferences entry is orphaned
/// by every update. Instead of a path we store a <see cref="Filter"/> — a glob whose
/// wildcard covers the version segment — and re-resolve it whenever the folder changes.
/// </summary>
public sealed class PinRule
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Glob for the executable, e.g. <c>%LOCALAPPDATA%\Discord\app-*\Discord.exe</c>.
    /// '*' and '?' match within a single path segment and never cross a separator, so a
    /// pattern cannot wander into a sibling tree. Environment variables are expanded.
    /// </summary>
    public string Filter { get; set; } = "";

    /// <summary>1 = power saving (iGPU), 2 = high performance (dGPU).</summary>
    public int Preference { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string ExpandedFilter => Environment.ExpandEnvironmentVariables(Filter);

    /// <summary>The registry value for this rule, e.g. "GpuPreference=1;".</summary>
    [JsonIgnore]
    public string RegistryValue => $"GpuPreference={Preference};";

    /// <summary>
    /// The deepest directory of the filter that contains no wildcard. This is what we
    /// hand to FileSystemWatcher: the updater creates the new version folder inside it.
    /// </summary>
    [JsonIgnore]
    public string WatchRoot
    {
        get
        {
            string[] segments = ExpandedFilter.Split(Path.DirectorySeparatorChar);
            var stable = new List<string>();
            foreach (string segment in segments)
            {
                if (segment.Contains('*') || segment.Contains('?')) break;
                stable.Add(segment);
            }

            // If nothing was wild the last segment is the filename itself, not a folder.
            if (stable.Count == segments.Length && stable.Count > 0)
            {
                stable.RemoveAt(stable.Count - 1);
            }

            return string.Join(Path.DirectorySeparatorChar, stable);
        }
    }
}

public sealed class PinRuleSet
{
    /// <summary>
    /// Serialised into rules.json ahead of the rules so the format documents itself.
    /// Get-only, so it is written on save and ignored on load: a stale or hand-mangled
    /// copy in an existing file cannot break parsing, and the current text is always
    /// what gets written back.
    /// </summary>
    [JsonPropertyName("_readme")]
    public IReadOnlyList<string> Readme => HelpLines;

    public List<PinRule> Rules { get; set; } = new();

    public static IReadOnlyList<string> HelpLines { get; } = new[]
    {
        "Each rule pins one application to a GPU by writing an entry under",
        "HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences.",
        "",
        "Only add a rule for an app installed under a VERSION-STAMPED folder. Anything with",
        "a stable install path, or an MSIX app (which is keyed by AUMID, not by path), can be",
        "pinned once in Windows Settings or PreferredGPUChanger and will never go stale.",
        "",
        "Name       : free text, shown in the tray menu.",
        "Filter     : glob for the executable.",
        "             '*' and '?' match inside ONE path segment and never cross a '\\'.",
        "             Environment variables are expanded, e.g. %LOCALAPPDATA%, %APPDATA%.",
        "             EVERY match is pinned, so an app that keeps its previous version",
        "             folder around is handled without extra rules.",
        "Preference : 1 = iGPU / power saving,  2 = dGPU / high performance.",
        "Enabled    : false keeps the rule here but stops all pinning and pruning for it.",
        "",
        "Pruning is deliberately narrow: an entry is removed only when it BOTH matches this",
        "rule's own Filter AND no longer exists on disk. Entries you made by hand, and apps",
        "no rule covers, are never touched.",
        "",
        "Edits here are picked up live - there is no need to restart HeadlessGPUKeeper.",
        "// and /* */ comments and trailing commas are accepted when reading this file.",
        "If it cannot be parsed at all, the built-in defaults are used until you fix it and",
        "the file on disk is left exactly as you wrote it.",
    };

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Hand-edited file: be forgiving about the things people actually type.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HeadlessGPUKeeper",
        "rules.json");

    /// <summary>
    /// Loads rules.json, writing the documented defaults on first run. A malformed file
    /// is left on disk untouched and the defaults are used for this session, so a typo
    /// never silently discards the user's edits.
    /// </summary>
    public static PinRuleSet Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<PinRuleSet>(File.ReadAllText(ConfigPath), JsonOptions);
                // An empty rule list is a legitimate choice — "pin nothing" — so it is
                // honoured rather than treated as a blank file to re-seed.
                if (loaded is not null) return loaded;
            }
            else
            {
                var defaults = Defaults();
                defaults.Save();
                return defaults;
            }
        }
        catch { /* fall through to defaults */ }

        return Defaults();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    /// <summary>
    /// The apps on this machine that rot: none is MSIX, none has an App Paths alias,
    /// and all install under a version-stamped directory. The disabled entry is a
    /// template to copy for a new app.
    /// </summary>
    public static PinRuleSet Defaults() => new()
    {
        Rules =
        {
            new PinRule
            {
                Name = "Microsoft Edge WebView2 Runtime",
                Filter = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\*\msedgewebview2.exe"
            },
            new PinRule
            {
                Name = "Discord",
                Filter = @"%LOCALAPPDATA%\Discord\app-*\Discord.exe"
            },
            new PinRule
            {
                Name = "Claude Code CLI",
                Filter = @"%APPDATA%\Claude\claude-code\*\claude.exe"
            },
            new PinRule
            {
                Name = "EXAMPLE - copy this shape for a new app, then set Enabled to true",
                Filter = @"%LOCALAPPDATA%\SomeVendor\SomeApp\*\SomeApp.exe",
                Preference = 1,
                Enabled = false
            }
        }
    };
}
