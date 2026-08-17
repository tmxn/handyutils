using System.Security.Cryptography;
using System.Text;

namespace WorkTracker.Services;

/// <summary>
/// Pass 2 — per-week LLM narrative. Within-developer comparison only;
/// cached in reports/ keyed by inputHash (hash:score pairs of the week).
/// </summary>
public sealed class InterpretService
{
    public const int PromptVersion = 4;

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
        var result = await _llm.RunAsync(prompt, ct);
        if (result.TimedOut)
            throw new InvalidOperationException($"LLM call timed out after {result.Duration.TotalSeconds:F0}s.");
        AppLog.Info($"week {weekStartStr} report generated in {sw.Elapsed.TotalSeconds:F0}s " +
                    $"(prompt {prompt.Length} chars)");

        var json = JsonExtract.ExtractFirstJsonObject(result.Stdout);
        if (json == null)
            throw new ScoreBatchFailedException(
                "LLM did not return a valid JSON report. See raw/ for details.",
                result, prompt);

        WeekNarrative narrative;
        try
        {
            narrative = System.Text.Json.JsonSerializer.Deserialize<WeekNarrative>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new InvalidOperationException("Empty narrative.");
        }
        catch (Exception ex)
        {
            throw new ScoreBatchFailedException(
                $"Report JSON could not be parsed: {ex.Message}", result, prompt);
        }
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
        sb.AppendLine("OUTPUT REQUIREMENTS (strict):");
        sb.AppendLine("- Write all text in plain ASCII: straight quotes/apostrophes, no curly quotes, no decorative dashes or ellipses.");
        sb.AppendLine("- This is review material with evidence and hypotheses, NOT a performance verdict. No goal-based evaluation, no employment verdicts, no cross-developer statements, no numeric ratings.");
        sb.AppendLine("- Every negative signal must carry evidence commit hashes and at least one alternative explanation (meetings, debugging, blocked time, etc.).");
        sb.AppendLine("- Questions must be specific and evidence-linked.");
        sb.AppendLine();
        sb.AppendLine("WEEK COMMITS (hash, date, subject, score, comment, stat):");
        foreach (var c in weekCommits.OrderBy(c => c.AuthorDate))
        {
            if (c.IsMerge)
            {
                sb.AppendLine($"{c.Hash} | {c.AuthorDate:yyyy-MM-dd} | [merge] {c.Subject}");
                continue;
            }
            var line = $"{c.Hash} | {c.AuthorDate:yyyy-MM-dd} | {c.Subject} | +{c.Insertions}/-{c.Deletions}";
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
        sb.AppendLine("Respond with ONLY valid JSON, no markdown fences, in exactly this shape:");
        sb.AppendLine("""
{"weekStart": "yyyy-MM-dd", "summary": "2-4 sentence activity summary", "notable": ["items worth manager attention, each tied to commit hashes"], "signals": [{"type": "possible_blocker|possible_struggle|revert_loop|wip_chain|other", "description": "...", "evidence": ["hash", "..."]}], "alternativeExplanations": ["..."], "questions": ["specific questions for a 1:1, evidence-linked"]}
""");
        return sb.ToString();
    }
}
