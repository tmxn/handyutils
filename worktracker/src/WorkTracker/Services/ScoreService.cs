using System.IO;
using System.Text;
using System.Text.Json;

namespace WorkTracker.Services;

/// <summary>
/// Pass 1 — absolute per-commit scoring.
/// One LLM call per (developer, day) of unscored commits; anchored by anchors/anchors.json;
/// results cached by commit hash in scores.json. Never guesses or backfills.
/// </summary>
public sealed class ScoreService
{
    public const int PromptVersion = 7;
    public const int MaxCommitsPerBatch = 15;

    // Diff triage: commits this large get a cheap pre-check (file list + short diff
    // sample only) so merge/mechanical trash diffs are not shipped to the scoring call.
    public const int TriageFileThreshold = 30;
    public const int TriageLineThreshold = 2000;
    private const int TriageDiffSampleLen = 3000;
    private const int TriageFileListLines = 150;
    private const int ScoringDiffSampleLen = 2000;
    private const int ScoringFileListLines = 100;

    private readonly LlmRunner _llm;

    public ScoreService(LlmRunner llm)
    {
        _llm = llm;
    }

    // ---------- anchors ----------

    public static AnchorSet LoadAnchors()
    {
        var path = FindAnchorsPath()
            ?? throw new FileNotFoundException(
                "anchors/anchors.json not found. Add it to the WorkTracker source repo.");
        return JsonSerializer.Deserialize<AnchorSet>(File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string? FindAnchorsPath()
    {
        // 1) Next to the executable (copied at build time).
        var p = Path.Combine(AppContext.BaseDirectory, "anchors", "anchors.json");
        if (File.Exists(p)) return p;
        // 2) Walk up from the base directory (developer checkouts).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            p = Path.Combine(dir.FullName, "anchors", "anchors.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    // ---------- scoring ----------

    public sealed record BatchResult(Dictionary<string, ScoreEntry> Scored)
    {
        public static BatchResult Empty() => new(new Dictionary<string, ScoreEntry>());
    }

    /// <summary>
    /// Scores the given (unscored) commits, which must all belong to one developer and one day.
    /// <paramref name="sameDayContext"/> = other commits of the same day already carrying scores
    /// (or scored earlier in this session) — used for difficulty inference only.
    /// Returns per-hash scores, or throws ScoreBatchFailedException after one retry.
    /// </summary>
    public async Task<BatchResult> ScoreBatchAsync(
        List<CommitInfo> commits,
        List<CommitInfo> sameDayContext,
        AnchorSet anchors,
        Action<string>? status = null,
        Action<string>? output = null,
        CancellationToken ct = default)
    {
        if (commits.Count == 0) return BatchResult.Empty();

        var mechanical = await TriageLargeCommitsAsync(commits, status, output, ct);
        var prompt = BuildPrompt(commits, sameDayContext, anchors, mechanical);
        AppLog.Info($"scoring batch: {commits.Count} commit(s), {mechanical.Count} triaged mechanical, " +
                    $"prompt {prompt.Length} chars");
        LlmResult? lastResult = null;
        string? lastParseError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lastResult = await _llm.RunAsync(prompt, ct, output);
            if (lastResult.TimedOut)
                throw new ScoreBatchFailedException(
                    $"LLM call timed out after {lastResult.Duration.TotalSeconds:F0}s.",
                    lastResult, prompt);

            var parseError = lastParseError = ValidateAndParse(lastResult.Stdout, commits);
            if (parseError == null)
            {
                var scored = Parse(lastResult.Stdout, commits)!;
                foreach (var (hash, note) in mechanical)
                    if (scored.TryGetValue(hash, out var e)) e.Triage = note;
                AppLog.Info("batch scored: " +
                    string.Join(" ", scored.Values.Select(e => e.Score.ToString())) +
                    " (" + string.Join(", ", commits.Select(c => c.ShortHash + "=" + scored[c.Hash].Score)) + ")");
                return new BatchResult(scored);
            }
            if (attempt == 0)
            {
                AppLog.Warn($"invalid LLM response, retrying once: {parseError}");
                status?.Invoke("Invalid LLM response, retrying once…");
                // Second attempt: same prompt (retry per spec).
            }
        }

        AppLog.Error($"scoring batch failed after 1 retry: {lastParseError}");
        throw new ScoreBatchFailedException(
            "LLM returned a response that could not be validated after 1 retry. " +
            "Commits were left unscored. See diagnostics for the raw output.",
            lastResult!, prompt);
    }

    private static string? ValidateAndParse(string stdout, List<CommitInfo> commits)
    {
        var json = JsonExtract.ExtractFirstJsonObject(stdout);
        if (json == null) return "No JSON object found in LLM output.";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arr = root.TryGetProperty("commits", out var a) ? a : throw new KeyNotFoundException("commits");
            if (arr.ValueKind != JsonValueKind.Array) return "'commits' is not an array.";

            var byHash = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("hash", out var h) || h.ValueKind != JsonValueKind.String)
                    return "A commit entry is missing its hash.";
                byHash[h.GetString()!] = el;
            }

            foreach (var c in commits)
            {
                if (!byHash.TryGetValue(c.Hash, out var el))
                    return $"Missing score for {c.ShortHash} ({c.Subject}).";
                if (!el.TryGetProperty("score", out var s) ||
                    (s.ValueKind != JsonValueKind.Number && s.ValueKind != JsonValueKind.String) ||
                    !int.TryParse(s.ToString(), out var score) ||
                    score < 1 || score > 10)
                    return $"Invalid score for {c.ShortHash}: '{s}'.";
                if (!el.TryGetProperty("comment", out var cm) ||
                    cm.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(cm.GetString()))
                    return $"Missing comment for {c.ShortHash}.";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"JSON parse error: {ex.Message}";
        }
    }

    private static Dictionary<string, ScoreEntry> Parse(string stdout, List<CommitInfo> commits)
    {
        var result = new Dictionary<string, ScoreEntry>(StringComparer.Ordinal);
        var json = JsonExtract.ExtractFirstJsonObject(stdout)!;
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.GetProperty("commits").EnumerateArray())
        {
            var hash = el.GetProperty("hash").GetString()!;
            result[hash] = new ScoreEntry
            {
                Score = int.Parse(el.GetProperty("score").ToString()),
                Comment = TextSanitizer.ToAscii(el.GetProperty("comment").GetString()),
                ScoredAt = DateTime.Now,
            };
        }
        return result;
    }

    // ---------- diff triage ----------

    /// <summary>
    /// A commit is "large" if it touches many files or many lines; those get triaged
    /// before their full diff is sent to the scoring call.
    /// </summary>
    public static bool NeedsTriage(CommitInfo c) =>
        c.FilesChanged >= TriageFileThreshold ||
        (c.FilesChanged >= 5 && c.Insertions + c.Deletions >= TriageLineThreshold);

    /// <summary>
    /// Cheap pre-check for large commits: one LLM call (separate from scoring) asking,
    /// per commit, whether the change looks like a merge / mechanical / generated change
    /// rather than real coding work. Only the changed-file list and a short diff sample
    /// are sent — never the full diff. Returns hash → reason for commits judged
    /// mechanical. Any failure (bad output, timeout, LLM error) falls back to "no
    /// triage": full diffs go to the scoring call as usual. Triage never blocks scoring.
    /// </summary>
    private async Task<Dictionary<string, string>> TriageLargeCommitsAsync(
        List<CommitInfo> commits, Action<string>? status, Action<string>? output, CancellationToken ct)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = commits.Where(c => NeedsTriage(c) && c.Diff != null).ToList();
        if (candidates.Count == 0) return empty;

        AppLog.Info("triage candidates: " +
            string.Join(" ", candidates.Select(c =>
                $"{c.ShortHash}({c.FilesChanged}f,+{c.Insertions}/-{c.Deletions})")));
        status?.Invoke($"triaging {candidates.Count} large commit(s) (file list + diff sample only)…");
        try
        {
            var result = await _llm.RunAsync(BuildTriagePrompt(candidates), ct, output);
            if (result.TimedOut)
            {
                AppLog.Warn("triage timed out — sending full diffs");
                status?.Invoke("triage timed out — sending full diffs");
                return empty;
            }
            var verdicts = ParseTriage(result.Stdout, candidates);
            foreach (var (hash, v) in verdicts)
                AppLog.Info($"triage {hash[..Math.Min(8, hash.Length)]}: " +
                    $"{(v.Mechanical ? $"MECHANICAL ({v.Reason})" : "not mechanical")}");
            var mechanical = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (hash, v) in verdicts)
                if (v.Mechanical) mechanical[hash] = v.Reason;
            status?.Invoke(mechanical.Count > 0
                ? $"{mechanical.Count}/{candidates.Count} large commit(s) triaged as mechanical — full diffs withheld"
                : "no large commits triaged as mechanical");
            return mechanical;
        }
        catch (Exception ex)
        {
            status?.Invoke($"triage failed ({ex.Message}) — sending full diffs");
            return empty;
        }
    }

    private static string BuildTriagePrompt(List<CommitInfo> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are triaging large git commits to decide whether they contain real hand-written coding work.");
        sb.AppendLine();
        sb.AppendLine("For each commit you are given its subject, body, the changed-file list with per-file insertions/deletions, and a short sample of the diff (only the beginning). The full diff is NOT included.");
        sb.AppendLine();
        sb.AppendLine("For each commit answer: does this change look like a merge / mechanical / generated change rather than substantive coding work?");
        sb.AppendLine("- Mechanical examples: dependency or lockfile bumps, bulk renames or moves, formatting sweeps, generated code, vendored third-party code, data/binary or doc dumps.");
        sb.AppendLine("- NOT mechanical: hand-written logic, even when spread across many files (refactors, features, bug fixes).");
        sb.AppendLine("- When unsure, answer mechanical=false — the full diff will then be reviewed.");
        sb.AppendLine();
        foreach (var c in candidates)
        {
            sb.AppendLine($"=== commit {c.Hash} ===");
            sb.AppendLine($"subject: {c.Subject}");
            if (!string.IsNullOrWhiteSpace(c.Body))
                sb.AppendLine($"body: {c.Body}");
            sb.AppendLine($"stat: {c.FilesChanged} files, added {c.Insertions} lines, removed {c.Deletions} lines");
            sb.AppendLine("changed files:");
            AppendFileList(sb, c, TriageFileListLines);
            sb.AppendLine("diff sample (beginning only):");
            sb.AppendLine(DiffSample(c.Diff!, TriageDiffSampleLen));
            sb.AppendLine();
        }
        sb.AppendLine("Respond with ONLY valid JSON, no markdown fences, no commentary:");
        sb.AppendLine("{\"commits\": [{\"hash\": \"…\", \"mechanical\": true, \"reason\": \"one short sentence\"}]}");
        return sb.ToString();
    }

    private static Dictionary<string, TriageVerdict> ParseTriage(string stdout, List<CommitInfo> candidates)
    {
        var json = JsonExtract.ExtractFirstJsonObject(stdout);
        if (json == null) throw new InvalidDataException("no JSON object in triage output");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.TryGetProperty("commits", out var a) ? a : throw new InvalidDataException("missing 'commits'");
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("'commits' is not an array");

        var byHash = new Dictionary<string, TriageVerdict>(StringComparer.Ordinal);
        foreach (var el in arr.EnumerateArray())
        {
            if (!el.TryGetProperty("hash", out var hEl) || hEl.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("a triage entry is missing its hash");
            if (!el.TryGetProperty("mechanical", out var mEl) ||
                (mEl.ValueKind != JsonValueKind.True && mEl.ValueKind != JsonValueKind.False))
                throw new InvalidDataException($"invalid 'mechanical' for {hEl.GetString()}");
            var reason = el.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String
                ? TextSanitizer.ToAscii(rEl.GetString()) : "";
            byHash[hEl.GetString()!] = new TriageVerdict(mEl.ValueKind == JsonValueKind.True, reason);
        }
        foreach (var c in candidates)
            if (!byHash.ContainsKey(c.Hash))
                throw new InvalidDataException($"missing triage verdict for {c.ShortHash} ({c.Subject})");
        return byHash;
    }

    // ---------- prompt ----------

    private static string DiffSample(string diff, int maxLen) =>
        diff.Length <= maxLen ? diff : diff.Substring(0, maxLen) + "\n[sample truncated]";

    private static void AppendFileList(StringBuilder sb, CommitInfo c, int maxLines)
    {
        for (var i = 0; i < c.Numstat.Count && i < maxLines; i++)
        {
            var n = c.Numstat[i];
            sb.AppendLine($"{n.File}  (added {n.Insertions} lines, removed {n.Deletions} lines)");
        }
        if (c.Numstat.Count > maxLines)
            sb.AppendLine($"[… {c.Numstat.Count - maxLines} more files]");
    }

    private static string BuildPrompt(List<CommitInfo> commits, List<CommitInfo> context, AnchorSet anchors,
        Dictionary<string, string> mechanical)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are scoring git commits on an absolute 1-10 effort scale.");
        sb.AppendLine();
        sb.AppendLine("SCALE — these reference commits define the scale. Score new commits relative to them.");
        foreach (var a in anchors.Anchors)
        {
            sb.AppendLine();
            sb.AppendLine($"[anchor {a.Id}, targetScore {a.TargetScore}]");
            sb.AppendLine($"note: {a.Note}");
            sb.AppendLine("diff:");
            sb.AppendLine(a.Diff);
        }
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Score the effort and complexity actually involved, not the size of the diff. A one-line fix of a subtle concurrency bug scores higher than 300 lines of generated or boilerplate changes.");
        sb.AppendLine("- Trivial one-line changes (flipping a boolean/return value, changing a constant or config value, updating a comment) score 2-3, even when a long explanatory comment is attached or the behavior being toggled matters — score the work the author did, not the importance of the outcome. An explanatory comment does not turn a one-liner into hard work.");
        sb.AppendLine("- Mechanical work (formatting, renames, dependency bumps, generated code) scores low.");
        sb.AppendLine("- Reverts are real work: score the effort of the revert plus the diagnosis it implies, and note in the comment that it is a revert.");
        sb.AppendLine("- Some commits are marked [triaged as mechanical]: a pre-check judged the change mechanical (e.g., dependency bumps, generated code, bulk renames), so their full diff is withheld. Score only the work such a commit implies, using the file list and diff sample — mechanical work scores low.");
        sb.AppendLine("- Integers 1-10 only. When unsure, use the anchors to decide which side of a boundary the commit falls on.");
        sb.AppendLine("- Give every commit an informative 1-2 sentence daily summary grounded in the diff. Name the behavior, algorithm, bug, or user-visible change and mention important files or components when clear. Add a difficulty note when notable (e.g., \"small diff but fixes a race condition\"). Do not merely restate the subject or describe the line count.");
        sb.AppendLine("- Write all text in plain ASCII: straight quotes/apostrophes, no curly quotes, no decorative dashes or ellipses.");
        sb.AppendLine();

        if (context.Count > 0)
        {
            sb.AppendLine("CONTEXT — same developer, same day (for difficulty inference only, never as scale references):");
            foreach (var c in context)
            {
                sb.AppendLine($"{c.Hash} | {c.Subject} | {c.FilesChanged} files, added {c.Insertions} lines, removed {c.Deletions} lines");
            }
            sb.AppendLine();
        }

        sb.AppendLine("TASK — score these commits (their diffs are included):");
        foreach (var c in commits)
        {
            sb.AppendLine();
            sb.AppendLine($"=== commit {c.Hash} ===");
            sb.AppendLine($"authorDate: {c.AuthorDate:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"subject: {c.Subject}");
            if (!string.IsNullOrWhiteSpace(c.Body))
                sb.AppendLine($"body: {c.Body}");
            sb.AppendLine($"stat: {c.FilesChanged} files, added {c.Insertions} lines, removed {c.Deletions} lines");
            if (mechanical.TryGetValue(c.Hash, out var triageNote))
            {
                sb.AppendLine("[triaged as mechanical — full diff withheld]");
                sb.AppendLine($"triage reason: {triageNote}");
                sb.AppendLine("changed files:");
                AppendFileList(sb, c, ScoringFileListLines);
                sb.AppendLine("diff sample (beginning only):");
                sb.AppendLine(c.Diff != null ? DiffSample(c.Diff, ScoringDiffSampleLen) : "(no diff available)");
            }
            else
            {
                sb.AppendLine("diff:");
                sb.AppendLine(c.Diff ?? "(no diff available)");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Respond with ONLY valid JSON, no markdown fences, no commentary:");
        sb.AppendLine("{\"commits\": [{\"hash\": \"…\", \"score\": 7, \"comment\": \"…\"}]}");
        return sb.ToString();
    }
}
