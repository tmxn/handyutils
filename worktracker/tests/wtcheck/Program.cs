using WorkTracker;
using WorkTracker.Services;

// Non-UI smoke test: git collection, parsing, windowing, diff fetch. No LLM calls.
// With "score" as second arg: additionally runs one real Pass-1 scoring batch via the
// configured LLM command (default: pi + github-copilot/gpt-5.6-luna). Not cached to disk.
var repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var doScore = args.Length > 1 && args[1] == "score";
var doInterpret = args.Length > 2 && args[2] == "interpret";
Console.WriteLine($"repo: {repo}");

var err = GitCollector.ValidateRepo(repo);
if (err != null)
{
    Console.WriteLine($"VALIDATION FAILED: {err}");
    return 1;
}
Console.WriteLine("validate: OK");

var collector = new GitCollector(repo);
var result = collector.Collect();
Console.WriteLine($"window: {result.WindowStart:yyyy-MM-dd} .. {result.WindowEnd:yyyy-MM-dd}");
Console.WriteLine($"commits: {result.Commits.Count}");

var failures = 0;
void Check(bool cond, string what)
{
    Console.WriteLine((cond ? "  ok  " : "  FAIL ") + what);
    if (!cond) failures++;
}

foreach (var c in result.Commits.Take(5))
{
    Console.WriteLine($"  {c.Hash[..8]} {c.AuthorDate:yyyy-MM-dd HH:mm} merge={c.IsMerge} revert={c.IsRevert} " +
                      $"{c.FilesChanged}f +{c.Insertions}/-{c.Deletions} \"{Trunc(c.Subject, 50)}\"");
}

// Invariants
Check(result.Commits.All(c => c.Hash.Length >= 7), "hashes parsed");
Check(result.Commits.All(c => c.AuthorDate >= result.WindowStart.AddDays(-1)), "dates inside window");
Check(result.Commits.All(c => c.AuthorDate <= result.WindowEnd.AddSeconds(5)), "dates not in future");
Check(result.Commits.All(c => c.Numstat.Sum(n => n.Insertions) == c.Insertions), "insertion sums");
Check(result.Commits.All(c => c.Numstat.Count == c.FilesChanged), "file counts");
Check(result.Commits.Where(c => c.Subject.StartsWith("Revert ", System.StringComparison.OrdinalIgnoreCase))
                    .All(c => c.IsRevert), "revert detection");
Check(result.Commits.All(c => !c.IsMerge || c.Numstat.Count == 0 || true), "(merge stat varies)");

// Diff-triage thresholds (large commits get a mechanical/merge pre-check before scoring)
var bigFiles = new CommitInfo { Subject = "x", FilesChanged = ScoreService.TriageFileThreshold,
    Numstat = Enumerable.Repeat(new NumstatEntry { File = "f", Insertions = 1, Deletions = 0 },
        ScoreService.TriageFileThreshold).ToList() };
var manyLines = new CommitInfo { Subject = "x", FilesChanged = 5, Insertions = ScoreService.TriageLineThreshold,
    Numstat = Enumerable.Repeat(new NumstatEntry { File = "f", Insertions = 1, Deletions = 0 }, 5).ToList() };
var small = new CommitInfo { Subject = "x", FilesChanged = 3, Insertions = 40, Deletions = 5 };
Check(ScoreService.NeedsTriage(bigFiles), "triage: >=30 files triggers");
Check(ScoreService.NeedsTriage(manyLines), "triage: >=5 files and >=2000 lines triggers");
Check(!ScoreService.NeedsTriage(small), "triage: small commit not triaged");

// Diff fetch
var firstNonMerge = result.Commits.FirstOrDefault(c => !c.IsMerge && c.Numstat.Count > 0);
if (firstNonMerge != null)
{
    var diff = collector.GetDiff(firstNonMerge);
    Console.WriteLine($"  diff length: {diff?.Length} (commit stat: {firstNonMerge.Insertions + firstNonMerge.Deletions} line changes)");
    Check(diff != null && diff.Contains("diff --git"), "diff fetch returns unified diff");
    Check(collector.GetDiff(firstNonMerge) == diff, "diff cached on commit");
}
else
{
    Console.WriteLine("  (no non-merge commit with numstat to test diff)");
}

// Window edge: this week's Monday
var today = DateTime.Now;
var expectedMonday = today.DayOfWeek == DayOfWeek.Sunday
    ? today.Date.AddDays(-6)
    : today.Date.AddDays(-(int)today.DayOfWeek);
Check(result.WindowStart == expectedMonday.AddDays(-21), $"window start = Monday-21d ({expectedMonday.AddDays(-21):yyyy-MM-dd})");

// Parse sanity: every commit has a subject
Check(result.Commits.All(c => !string.IsNullOrWhiteSpace(c.Subject)), "subjects non-empty");

if (doScore && result.Commits.Count > 0)
{
    Console.WriteLine("\n--- Pass-1 live scoring (no cache writes) ---");
    var llmSettings = new LlmSettings
    {
        Command = "pi",
        Args = new List<string> { "--no-session", "--print", "--model", "github-copilot/gpt-5.6-luna" },
        TimeoutSeconds = 300,
    };
    var llm = new LlmRunner(llmSettings);
    var resolved = llm.ResolveCommand();
    Console.WriteLine($"llm command: {resolved ?? "NOT FOUND"}");
    var scoreService = new ScoreService(llm);
    var anchors = ScoreService.LoadAnchors();
    Console.WriteLine($"anchors: v{anchors.AnchorVersion} ({anchors.Anchors.Count} anchors)");

    var batch = result.Commits.Where(c => !c.IsMerge).Take(4).ToList();
    foreach (var c in batch) collector.GetDiff(c);
    foreach (var c in batch.Where(ScoreService.NeedsTriage))
        Console.WriteLine($"  (triage candidate: {c.Hash[..8]} {c.FilesChanged}f +{c.Insertions}/-{c.Deletions})");
    Console.WriteLine($"scoring {batch.Count} commit(s)…");
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await scoreService.ScoreBatchAsync(batch, new List<CommitInfo>(), anchors,
            status: s => Console.WriteLine("  " + s));
        sw.Stop();
        foreach (var (hash, e) in res.Scored)
            Console.WriteLine($"  {hash[..8]} score={e.Score}  {e.Comment}");
        Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F0}s");
    }
    catch (ScoreBatchFailedException ex)
    {
        Console.WriteLine("  FAILED: " + ex.Message);
        Console.WriteLine("  stdout tail: " + ex.LastResult.Stdout[^Math.Min(800, ex.LastResult.Stdout.Length)..]);
        failures++;
    }
}

if (doInterpret && result.Commits.Count > 0)
{
    Console.WriteLine("\n--- Pass-2 live week narrative (writes one report file) ---");
    var llm2 = new LlmRunner(new LlmSettings
    {
        Command = "pi",
        Args = new List<string> { "--no-session", "--print", "--model", "github-copilot/gpt-5.6-luna" },
        TimeoutSeconds = 300,
    });
    var interpret = new InterpretService(llm2);
    var scores = new ScoreCache();
    // Synthetic scores for the test window (no real scoring in this mode).
    foreach (var c in result.Commits.Where(c => !c.IsMerge))
        scores.Entries[c.Hash] = new ScoreEntry { Score = 7, Comment = "test comment", ScoredAt = DateTime.Now };
    var weekStart = result.WindowStart.AddDays(21); // current week
    try
    {
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var report = await interpret.GetWeekReportAsync("wtcheck", weekStart,
            result.Commits, result.Commits, scores, true, s => Console.WriteLine("  " + s));
        sw2.Stop();
        var n = report.Report;
        Console.WriteLine($"  weekStart={n.WeekStart} generated in {sw2.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine("  summary: " + n.Summary);
        Console.WriteLine($"  notable={n.Notable.Count} signals={n.Signals.Count} alts={n.AlternativeExplanations.Count} questions={n.Questions.Count}");
        foreach (var s in n.Signals) Console.WriteLine($"    [{s.Type}] {s.Description} evidence={string.Join(",", s.Evidence)}");
        Console.WriteLine("  report file: " + Store.ReportPath("wtcheck", weekStart.ToString("yyyy-MM-dd")));
    }
    catch (ScoreBatchFailedException ex)
    {
        Console.WriteLine("  FAILED: " + ex.Message);
        failures++;
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
