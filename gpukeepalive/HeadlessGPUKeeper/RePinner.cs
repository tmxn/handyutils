using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HeadlessGpuKeeper;

public sealed record SyncReport(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public bool Changed => Added.Count > 0 || Removed.Count > 0;
    public static SyncReport Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Keeps UserGpuPreferences in step with rules whose executables move between versions.
///
/// For each rule the glob is expanded against the live filesystem, entries are written
/// for every match, and entries the same glob would have produced but whose file is gone
/// are removed. Pruning is deliberately narrow: an entry is only ever deleted if it both
/// matches the rule's own pattern and no longer exists on disk, so hand-made entries and
/// unrelated apps are never touched.
/// </summary>
public static class RePinner
{
    const string RegPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    static readonly object SyncLock = new();

    public static DateTime LastSyncUtc { get; private set; }

    /// <summary>
    /// Expands a glob to the executables that currently exist. Wildcards are matched one
    /// path segment at a time, so a pattern never escapes into a sibling tree.
    /// </summary>
    public static IReadOnlyList<string> Expand(string pattern)
    {
        string[] segments = pattern.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return Array.Empty<string>();

        var current = new List<string> { segments[0] + Path.DirectorySeparatorChar };

        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i];
            bool isLast = i == segments.Length - 1;
            bool isWild = segment.Contains('*') || segment.Contains('?');
            var next = new List<string>();

            foreach (string dir in current)
            {
                try
                {
                    if (isWild)
                    {
                        next.AddRange(isLast
                            ? Directory.EnumerateFiles(dir, segment)
                            : Directory.EnumerateDirectories(dir, segment));
                    }
                    else
                    {
                        string combined = Path.Combine(dir, segment);
                        bool exists = isLast ? File.Exists(combined) : Directory.Exists(combined);
                        if (exists) next.Add(combined);
                    }
                }
                catch
                {
                    // Unreadable directory (permissions, race with an updater): skip it.
                }
            }

            current = next;
            if (current.Count == 0) break;
        }

        return current;
    }

    /// <summary>Applies every enabled rule and prunes entries their globs have outlived.</summary>
    public static SyncReport Sync(PinRuleSet ruleSet)
    {
        lock (SyncLock)
        {
            var added = new List<string>();
            var removed = new List<string>();

            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RegPath);
                if (key is null) return SyncReport.Empty;

                foreach (PinRule rule in ruleSet.Rules)
                {
                    if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Filter)) continue;

                    string expanded = rule.ExpandedFilter;
                    IReadOnlyList<string> matches = Expand(expanded);

                    foreach (string match in matches)
                    {
                        // Skip writes that would not change anything, so an idle sweep is
                        // silent and the tray balloon only fires on a real update.
                        if (key.GetValue(match) as string == rule.RegistryValue) continue;
                        key.SetValue(match, rule.RegistryValue, RegistryValueKind.String);
                        added.Add(match);
                    }

                    Regex shape = ToRegex(expanded);
                    foreach (string name in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!shape.IsMatch(name)) continue;
                        if (matches.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (File.Exists(name)) continue;

                        key.DeleteValue(name, throwOnMissingValue: false);
                        removed.Add(name);
                    }
                }
            }
            catch
            {
                // A failed sweep is recoverable: the watcher and the periodic sweep retry.
            }

            LastSyncUtc = DateTime.UtcNow;
            return new SyncReport(added, removed);
        }
    }

    /// <summary>
    /// Turns a glob into an anchored regex where a wildcard cannot cross a path
    /// separator, so "...\Application\*\msedgewebview2.exe" can never match something
    /// nested deeper than the version folder it was written for.
    /// </summary>
    static Regex ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        string separator = Regex.Escape(Path.DirectorySeparatorChar.ToString());

        foreach (char c in pattern)
        {
            builder.Append(c switch
            {
                '*' => $"[^{separator}]*",
                '?' => $"[^{separator}]",
                _ => Regex.Escape(c.ToString())
            });
        }

        return new Regex(builder.Append('$').ToString(), RegexOptions.IgnoreCase);
    }
}
