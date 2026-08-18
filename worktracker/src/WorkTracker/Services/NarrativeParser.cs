using System.Text.RegularExpressions;

namespace WorkTracker.Services;

/// <summary>
/// Parses the plain-text week report format (##Summary / ##Notable / ##Signals / ...).
/// Lenient by design: models occasionally drift on formatting, so this accepts
/// case-insensitive markers, bullet-less lists, numbered lists, stray colons, and
/// missing evidence. Only a missing/empty ##Summary (or no recognizable section
/// at all) is a hard error.
/// </summary>
internal static class NarrativeParser
{
    private static readonly string[] ValidSignalTypes =
    {
        "possible_blocker", "possible_struggle", "revert_loop", "wip_chain", "other",
    };

    // 7..40 hex chars: full or abbreviated commit hashes as listed in the prompt.
    private static readonly Regex HashToken = new(@"\b[0-9a-fA-F]{7,40}\b", RegexOptions.Compiled);

    private static readonly string[] Negatives =
    {
        "none", "none.", "no signals", "n/a", "none applicable", "no",
    };

    public static WeekNarrative Parse(string raw)
    {
        var summary = new List<string>();
        var notable = new List<string>();
        var signals = new List<string>();
        var alt = new List<string>();
        var questions = new List<string>();
        var sawSection = false;
        string? current = null; // known section name, or null when outside/unknown

        foreach (var rawLine in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                current = SectionName(line);
                if (current != null) sawSection = true;
                continue;
            }
            if (current == null || string.IsNullOrWhiteSpace(line))
                continue;

            var item = StripBullet(line);
            if (item == null)
                continue;
            switch (current)
            {
                case "summary":
                    summary.Add(item);
                    break;
                case "notable":
                    if (!IsNegative(item)) notable.Add(item);
                    break;
                case "signals":
                    if (!IsNegative(item)) signals.Add(item);
                    break;
                case "alternativeexplanations":
                    if (!IsNegative(item)) alt.Add(item);
                    break;
                case "questions":
                    if (!IsNegative(item)) questions.Add(item);
                    break;
            }
        }

        if (!sawSection)
            throw new FormatException("no recognized section markers (##Summary, ##Notable, ...) found in the report");

        var n = new WeekNarrative
        {
            Summary = string.Join(" ", summary).Trim(),
            Notable = notable,
            Signals = signals.Select(ParseSignal).ToList(),
            AlternativeExplanations = alt,
            Questions = questions,
        };
        if (n.Summary.Length == 0)
            throw new FormatException("report is missing a non-empty ##Summary section");
        return n;
    }

    /// <summary>Maps a "##Something" line to a known section name, or null if unrecognized.</summary>
    private static string? SectionName(string line)
    {
        var rest = line.Substring(2).Trim().TrimEnd(':').Trim();
        if (rest.Length == 0)
            return null;
        int i = 0;
        while (i < rest.Length && (char.IsLetterOrDigit(rest[i]) || rest[i] == '_'))
            i++;
        if (i == 0)
            return null;
        return rest[..i].ToLowerInvariant() switch
        {
            "summary" => "summary",
            "notable" => "notable",
            "signals" => "signals",
            "alternativeexplanations" or "alternative" or "alternatives" or "explanations"
                => "alternativeexplanations",
            "questions" => "questions",
            _ => null,
        };
    }

    /// <summary>Strips a leading bullet ("- ", "* ", "• ", "1. ", "1) ") if present; null if nothing left.</summary>
    private static string? StripBullet(string line)
    {
        var l = line.Trim();
        if (l.Length == 0)
            return null;
        var c = l[0];
        if (c is '-' or '*' or '•' or '–' or '—')
        {
            var rest = l.Substring(1).Trim();
            return rest.Length > 0 ? rest : null;
        }
        if (char.IsDigit(c))
        {
            int i = 0;
            while (i < l.Length && char.IsDigit(l[i]))
                i++;
            if (i < l.Length && (l[i] == '.' || l[i] == ')'))
            {
                var rest = l.Substring(i + 1).Trim();
                if (rest.Length > 0)
                    return rest;
            }
        }
        return l;
    }

    private static bool IsNegative(string item)
    {
        return Negatives.Contains(item.Trim().TrimEnd('.', '!').ToLowerInvariant());
    }

    /// <summary>Parses "- [type] description. Evidence: hash1, hash2" (all parts optional and order-tolerant).</summary>
    private static ReportSignal ParseSignal(string line)
    {
        var s = new ReportSignal();
        var t = line.Trim();

        if (t.StartsWith('[') && t.IndexOf(']') > 0)
        {
            var type = t[1..t.IndexOf(']')].Trim().ToLowerInvariant().Replace(" ", "_");
            t = t[(t.IndexOf(']') + 1)..].Trim();
            s.Type = ValidSignalTypes.Contains(type) ? type : "other";
        }

        string desc = t;
        var evIdx = IndexOfOrdinalIgnoreCase(t, "evidence");
        if (evIdx >= 0)
        {
            desc = t[..evIdx].Trim().TrimEnd('|', '-', ':', ' ');
            var evPart = t[(evIdx + "evidence".Length)..].TrimStart(':', '|', '-', ' ');
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in HashToken.Matches(evPart))
            {
                if (seen.Add(m.Value))
                    s.Evidence.Add(m.Value);
            }
        }

        s.Description = desc;
        return s;
    }

    private static int IndexOfOrdinalIgnoreCase(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
}
