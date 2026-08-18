using System.Security.Cryptography;
using System.Text;

namespace WorkTracker.Services;

/// <summary>
/// Pass 2 — per-week LLM narrative. Within-developer comparison only;
/// cached in reports/ keyed by inputHash (hash:score pairs of the week).
/// </summary>
public sealed class InterpretService
{
    public const int PromptVersion = 6;

    private readonly LlmRunner _llm;

    public InterpretService(LlmRunner llm)
    {
        _llm = llm;
    }

    /// <summary>
    /// Returns the cached report if fresh, otherwise generates and stores a new one.
    /// <paramref name="forceRegenerate"/> bypasses the cache (explicit "Regenerate report").
    /// </summary>
    public async Task<ReportFile> GetWeekReportAsync(
        string developerId,
        DateTime weekStart,
        List<CommitInfo> weekCommits,        // scored commits of the target week
        List<CommitInfo> allWindowCommits,   // whole 4-week window (for per-day context)
        ScoreCache scores,
        bool forceRegenerate,
        Action<string>? status = null,
        Action<string>? output = null,
        CancellationToken ct = default)
    {
        var weekStartStr = weekStart.ToString("yyyy-MM-dd");
        var inputHash = ComputeInputHash(weekCommits, scores);

        if (!forceRegenerate)
        {
            var cached = Store.LoadReport(developerId, weekStartStr);
            if (cached != null &&
                cached.InputHash == inputHash &&
                cached.PromptVersion == PromptVersion)
            {
                AppLog.Info($"week {weekStartStr} report served from cache ({cached.GeneratedAt:yyyy-MM-dd HH:mm})");
                status?.Invoke("Report served from cache.");
                return cached;
            }
        }

        AppLog.Info($"week {weekStartStr} report: generating ({(forceRegenerate ? "forced" : "stale/missing")}, " +
                    $"{weekCommits.Count} commits in week)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        status?.Invoke("Generating week narrative (LLM call)…");
        var prompt = BuildPrompt(developerId, weekStart, weekCommits, allWindowCommits, scores);
        var result = await _llm.RunAsync(prompt, ct, output);
        if (result.TimedOut)
            throw new InvalidOperationException($"LLM call timed out after {result.Duration.TotalSeconds:F0}s.");
        AppLog.Info($"week {weekStartStr} report generated in {sw.Elapsed.TotalSeconds:F0}s " +
                    $"(prompt {prompt.Length} chars)");

        WeekNarrative narrative;
        try
        {
            narrative = NarrativeParser.Parse(result.Stdout);
        }
        catch (Exception ex)
        {
            throw new ScoreBatchFailedException(
                $"Report could not be parsed: {ex.Message}. See raw/ for details.", result, prompt);
        }
        narrative.WeekStart = weekStartStr;
        Sanitize(narrative);

        var report = new ReportFile
        {
            Developer = developerId,
            WeekStart = weekStartStr,
            PromptVersion = PromptVersion,
            InputHash = inputHash,
            GeneratedAt = DateTime.Now,
            Report = narrative,
        };
        Store.SaveReport(report);
        return report;
    }

    private static void Sanitize(WeekNarrative n)
    {
        n.WeekStart = TextSanitizer.ToAscii(n.WeekStart);
        n.Summary = TextSanitizer.ToAscii(n.Summary);
        n.Notable = n.Notable.Select(TextSanitizer.ToAscii).ToList();
        n.AlternativeExplanations = n.AlternativeExplanations.Select(TextSanitizer.ToAscii).ToList();
        n.Questions = n.Questions.Select(TextSanitizer.ToAscii).ToList();
        foreach (var s in n.Signals)
        {
            s.Type = TextSanitizer.ToAscii(s.Type);
            s.Description = TextSanitizer.ToAscii(s.Description);
            s.Evidence = s.Evidence.Select(TextSanitizer.ToAscii).ToList();
        }
    }

    /// <summary>SHA-256 over the sorted hash:score pairs of the week's scored commits.</summary>
    public static string ComputeInputHash(List<CommitInfo> commits, ScoreCache scores)
    {
        // Canonical string: sorted "hash:score" pairs of the week's scored, non-merge commits.
        var canonical = string.Join("\n",
            commits
                .Where(c => !c.IsMerge && scores.Entries.TryGetValue(c.Hash, out var e))
                .Select(c => c.Hash + ":" + scores.Entries[c.Hash].Score)
                .OrderBy(x => x, StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return "sha256:" + hash;
    }

    private static string BuildPrompt(
        string developerId, DateTime weekStart,
        List<CommitInfo> weekCommits,
        List<CommitInfo> allWindowCommits,
        ScoreCache scores)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are preparing 1:1 review material for a manager about developer '{developerId}' for the week starting {weekStart:yyyy-MM-dd}.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT (strict — plain text, NOT JSON):");
        sb.AppendLine("- Write all text in plain ASCII: straight quotes/apostrophes, no curly quotes, no decorative dashes or ellipses.");
        sb.AppendLine("- Use exactly these five sections, each marker on its own line, in this order:");
        sb.AppendLine("  ##Summary");
        sb.AppendLine("  ##Notable");
        sb.AppendLine("  ##Signals");
        sb.AppendLine("  ##AlternativeExplanations");
        sb.AppendLine("  ##Questions");
        sb.AppendLine("- ##Summary: 2-4 sentence activity summary.");
        sb.AppendLine("- ##Notable: bullet list (one \\\"- item\\\" per line), each item tied to commit hashes.");
        sb.AppendLine("- ##Signals: one bullet per line in exactly this shape: - [type] description. Evidence: hash1, hash2");
        sb.AppendLine("  where type is one of: possible_blocker | possible_struggle | revert_loop | wip_chain | other.");
        sb.AppendLine("  If there are no signals, write the single word \\\"none\\\" under the marker.");
        sb.AppendLine("- ##AlternativeExplanations: bullet list of alternative explanations for the signals (meetings, debugging, blocked time, etc.).");
        sb.AppendLine("- ##Questions: bullet list of specific, evidence-linked questions for a 1:1.");
        sb.AppendLine("- No JSON, no code fences, no sections other than the five listed.");
        sb.AppendLine("- This is review material with evidence and hypotheses, NOT a performance verdict. No goal-based evaluation, no employment verdicts, no cross-developer statements, no numeric ratings.");
        sb.AppendLine();
        sb.AppendLine("WEEK COMMITS (hash, date, subject, score, comment, stat):");
        foreach (var c in weekCommits.OrderBy(c => c.AuthorDate))
        {
            if (c.IsMerge)
            {
                sb.AppendLine($"{c.Hash} | {c.AuthorDate:yyyy-MM-dd} | [merge] {c.Subject}");
                continue;
            }
            var line = $"{c.Hash} | {c.AuthorDate:yyyy-MM-dd} | {c.Subject} | added {c.Insertions} lines, removed {c.Deletions} lines";
            if (scores.Entries.TryGetValue(c.Hash, out var e))
                line += $" | score {e.Score} | {e.Comment}";
            else
                line += " | (unscored)";
            sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("OTHER WEEKS OF THE 4-WEEK WINDOW, aggregated per day (load + commit count) — for within-developer comparison only:");
        var byDay = allWindowCommits
            .GroupBy(c => c.AuthorDate.Date)
            .OrderBy(g => g.Key)
            .Where(g => g.Key != weekStart.Date);
        foreach (var g in byDay)
        {
            var load = g.Where(c => !c.IsMerge && scores.Entries.ContainsKey(c.Hash))
                        .Sum(c => scores.Entries[c.Hash].Score);
            var count = g.Count(c => !c.IsMerge);
            sb.AppendLine($"{g.Key:yyyy-MM-dd} | load {load} | {count} commits");
        }
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY the plain-text report described above. Start directly with ##Summary.");
        return sb.ToString();
    }
}
