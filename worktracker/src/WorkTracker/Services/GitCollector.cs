using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkTracker.Services;

/// <summary>Spawns git and builds CommitInfo[] for the 4-week window.</summary>
public sealed class GitCollector
{
    private static readonly string HeaderMarker = "@@WT@@@";
    private const char Sep = '\u001f';
    private static readonly Regex NumstatRegex = new(@"^(\d+|-)\t(\d+|-)\t(.+)$", RegexOptions.Compiled);

    private readonly string _repoPath;

    public GitCollector(string repoPath)
    {
        _repoPath = repoPath;
    }

    public static string? ValidateRepo(string repoPath)
    {
        // Returns null on success, or the exact git error message.
        try
        {
            var r = RunGit(new[] { "-C", repoPath, "rev-parse", "--git-dir" }, 15);
            return r.ExitCode == 0 ? null : r.Stderr.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Collects all commits in [windowStart, now] across all refs.
    /// windowStart is the Monday of the week three weeks before this week (local time).
    /// </summary>
    public GitCollectionResult Collect()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTime.Now;
        var thisWeekStart = StartOfWeek(now);
        var windowStart = thisWeekStart.AddDays(-21);

        var r = RunGit(new[]
        {
            "-C", _repoPath, "log", "--all", "--numstat", "--date=iso-strict",
            "--since=" + windowStart.AddDays(-1).ToString("yyyy-MM-dd"),
            $"--format={HeaderMarker}%H{Sep}%an{Sep}%ae{Sep}%aI{Sep}%P{Sep}%s",
        }, 120);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"git log failed: {r.Stderr.Trim()}");

        var commits = ParseLog(r.Stdout, windowStart);
        AppLog.Info($"git log: {commits.Count} commits in window " +
                    $"({windowStart:yyyy-MM-dd}..{now:yyyy-MM-dd}, {sw.ElapsedMilliseconds}ms)");
        return new GitCollectionResult
        {
            WindowStart = windowStart,
            WindowEnd = now,
            Commits = commits,
        };
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        var day = d.Date;
        // ISO-style: Monday starts the week (Sunday belongs to the previous week).
        return d.DayOfWeek == DayOfWeek.Sunday
            ? day.AddDays(-6)
            : day.AddDays(-(int)d.DayOfWeek);
    }

    private static List<CommitInfo> ParseLog(string output, DateTime windowStart)
    {
        var result = new List<CommitInfo>();
        CommitInfo? current = null;
        var body = new StringBuilder();
        var inNumstat = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith(HeaderMarker))
            {
                FlushCurrent(ref current, body, result, windowStart);
                body.Clear();
                inNumstat = false;

                var fields = line.Substring(HeaderMarker.Length).Split(Sep);
                if (fields.Length < 6) continue;
                var parents = fields[4].Length == 0 ? Array.Empty<string>() : fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var subject = fields[5];

                current = new CommitInfo
                {
                    Hash = fields[0],
                    AuthorName = fields[1],
                    AuthorEmail = fields[2],
                    AuthorDate = DateTime.Parse(fields[3]),
                    Subject = subject,
                    IsMerge = parents.Length >= 2,
                    IsRevert = Regex.IsMatch(subject, @"^Revert ", RegexOptions.IgnoreCase),
                };
                continue;
            }

            if (current == null) continue;

            if (inNumstat)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    // End of this commit's numstat block (body already consumed).
                    continue;
                }
                var m = NumstatRegex.Match(line);
                if (m.Success)
                {
                    current.Numstat.Add(new NumstatEntry
                    {
                        File = m.Groups[3].Value,
                        Insertions = m.Groups[1].Value == "-" ? 0 : int.Parse(m.Groups[1].Value),
                        Deletions = m.Groups[2].Value == "-" ? 0 : int.Parse(m.Groups[2].Value),
                    });
                }
                continue;
            }

            var m2 = NumstatRegex.Match(line);
            if (m2.Success)
            {
                inNumstat = true;
                // Commit body is what we collected before the first numstat line.
                current.Body = body.ToString().Trim();
                if (current.Body.Length > 500) current.Body = current.Body.Substring(0, 500);
                body.Clear();
                current.Numstat.Add(new NumstatEntry
                {
                    File = m2.Groups[3].Value,
                    Insertions = m2.Groups[1].Value == "-" ? 0 : int.Parse(m2.Groups[1].Value),
                    Deletions = m2.Groups[2].Value == "-" ? 0 : int.Parse(m2.Groups[2].Value),
                });
                continue;
            }

            // Body line.
            body.Append(line).Append('\n');
        }

        FlushCurrent(ref current, body, result, windowStart);
        return result;
    }

    private static void FlushCurrent(ref CommitInfo? current, StringBuilder body, List<CommitInfo> result, DateTime windowStart)
    {
        if (current == null) return;
        if (body.Length > 0 && current.Numstat.Count == 0)
        {
            current.Body = body.ToString().Trim();
            if (current.Body.Length > 500) current.Body = current.Body.Substring(0, 500);
        }
        current.FilesChanged = current.Numstat.Count;
        current.Insertions = current.Numstat.Sum(n => n.Insertions);
        current.Deletions = current.Numstat.Sum(n => n.Deletions);
        // Bucket by author date in local time; keep commits inside the window.
        if (current.AuthorDate >= windowStart.AddDays(-1).AddHours(12))
            result.Add(current);
        current = null;
    }

    /// <summary>Fetches the diff for one commit (lazy second pass), truncated to 15,000 chars.</summary>
    public string? GetDiff(CommitInfo commit)
    {
        if (commit.Diff != null) return commit.Diff;
        var r = RunGit(new[] { "-C", _repoPath, "show", "--format=", "--no-color", commit.Hash }, 30);
        if (r.ExitCode != 0)
        {
            AppLog.Warn($"git show {commit.Hash[..Math.Min(8, commit.Hash.Length)]} failed: {r.Stderr.Trim()}");
            return null;
        }
        const int MaxLen = 15000;
        var text = r.Stdout;
        if (text.Length > MaxLen)
        {
            AppLog.Info($"diff {commit.Hash[..Math.Min(8, commit.Hash.Length)]} truncated " +
                        $"{r.Stdout.Length} -> {MaxLen} chars ({commit.FilesChanged} files, " +
                        $"+{commit.Insertions}/-{commit.Deletions})");
            text = text.Substring(0, MaxLen) + "\n[truncated]";
        }
        commit.Diff = text;
        return text;
    }

    private static ProcessResult RunGit(string[] gitArgs, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in gitArgs) psi.ArgumentList.Add(a);

        var desc = string.Join(' ', gitArgs); // local diagnostics only, never leaves the machine
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            AppLog.Error($"git timed out after {timeoutSeconds}s: {desc}");
            throw new TimeoutException("git command timed out");
        }
        sw.Stop();
        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        AppLog.Info($"git exit={p.ExitCode} {sw.ElapsedMilliseconds}ms out={outText.Length}B: {desc}");
        if (p.ExitCode != 0)
            AppLog.Warn($"git stderr: {Tail(errText, 500)}");
        return new ProcessResult(p.ExitCode, outText, errText);
    }

    private static string Tail(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[^n..]);

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
}
