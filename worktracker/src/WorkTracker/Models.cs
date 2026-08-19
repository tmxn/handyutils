using System.Text;

namespace WorkTracker;

// ---------- Config ----------

public sealed class Developer
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> AuthorNames { get; set; } = new();
    public List<string> AuthorEmails { get; set; } = new();

    public bool Matches(string authorName, string authorEmail)
    {
        static bool Match(string pattern, string value) =>
            !string.IsNullOrEmpty(pattern) &&
            !string.IsNullOrEmpty(value) &&
            value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        foreach (var n in AuthorNames) if (Match(n, authorName)) return true;
        foreach (var e in AuthorEmails) if (Match(e, authorEmail)) return true;
        return false;
    }
}

public sealed class LlmSettings
{
    // "pi" keeps the existing process-backed integration; "llama.cpp" uses its
    // local OpenAI-compatible chat-completions endpoint.
    public string Backend { get; set; } = "pi";
    public string Command { get; set; } = "pi";
    public List<string> Args { get; set; } = new() { "--no-session", "--print" };
    public int TimeoutSeconds { get; set; } = 600;
    // pi's --thinking level. Empty/"" = don't inject (leave as the CLI default).
    // Values: off, minimal, low, medium, high, xhigh, max. Applied only when the
    // resolved command looks like pi; user-provided args may still override.
    public string ThinkingEffort { get; set; } = "medium";

    // llama.cpp server settings. Authentication is intentionally not part of the
    // MVP; local llama.cpp servers do not need it.
    public string LlamaEndpoint { get; set; } = "http://192.168.18.126:8080/";
    public string LlamaModel { get; set; } = "any";
    public string LlamaThinkingLevel { get; set; } = "low";
}

public sealed class GridSettings
{
    // 10 cutoffs producing 11 steps (step 0 = empty). Green steps up to 24,
    // cyan from 25+, deep cyan from 30+.
    public List<int> LoadThresholds { get; set; } = new() { 0, 1, 4, 7, 10, 13, 16, 20, 25, 30 };
}

public sealed class AppConfig
{
    public string RepoPath { get; set; } = "";
    public List<Developer> Developers { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public GridSettings Grid { get; set; } = new();
    // "auto" (follow OS), "light", or "dark".
    public string Theme { get; set; } = "auto";
}

// ---------- Git ----------

public sealed class NumstatEntry
{
    public string File { get; set; } = "";
    public int Insertions { get; set; }
    public int Deletions { get; set; }
}

public sealed class CommitInfo
{
    public string Hash { get; set; } = "";
    public string ShortHash => Hash.Length >= 6 ? Hash.Substring(0, 6) : Hash;
    public string AuthorName { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public DateTime AuthorDate { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsMerge { get; set; }
    public bool IsRevert { get; set; }
    public int FilesChanged { get; set; }
    public int Insertions { get; set; }
    public int Deletions { get; set; }
    public List<NumstatEntry> Numstat { get; set; } = new();
    // Lazy, only fetched when needed for scoring.
    public string? Diff { get; set; }
}

public sealed class GitCollectionResult
{
    public List<CommitInfo> Commits { get; set; } = new();
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
}

// ---------- Score cache ----------

public sealed class ScoreEntry
{
    public int Score { get; set; }
    public string Comment { get; set; } = "";
    public DateTime ScoredAt { get; set; }
    // Set when diff triage judged the commit mechanical (full diff withheld from the scoring call).
    public string? Triage { get; set; }
}

/// <summary>Result of the large-diff triage pre-check for one commit.</summary>
public sealed record TriageVerdict(bool Mechanical, string Reason = "");

public sealed class ScoreCache
{
    public int Version { get; set; } = 1;
    public int AnchorVersion { get; set; } = 1;
    public int PromptVersion { get; set; } = 1;
    public Dictionary<string, ScoreEntry> Entries { get; set; } = new();
}

// ---------- Anchors ----------

public sealed class Anchor
{
    public string Id { get; set; } = "";
    public int TargetScore { get; set; }
    public string Note { get; set; } = "";
    public string Diff { get; set; } = "";
}

public sealed class AnchorSet
{
    public int AnchorVersion { get; set; } = 1;
    public List<Anchor> Anchors { get; set; } = new();
}

// ---------- Pass 2 report ----------

public sealed class ReportSignal
{
    public string Type { get; set; } = "other";
    public string Description { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
}

public sealed class WeekNarrative
{
    public string WeekStart { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Notable { get; set; } = new();
    public List<ReportSignal> Signals { get; set; } = new();
    public List<string> AlternativeExplanations { get; set; } = new();
    public List<string> Questions { get; set; } = new();
}

public sealed class ReportFile
{
    public string Developer { get; set; } = "";
    public string WeekStart { get; set; } = "";
    public int PromptVersion { get; set; }
    public string InputHash { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public WeekNarrative Report { get; set; } = new();
}

// ---------- LLM ----------

public sealed class LlmResult
{
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public bool TimedOut { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class LlmResolveError : Exception
{
    public LlmResolveError(string message) : base(message) { }
}

// ---------- Errors ----------

public sealed class ScoreBatchFailedException : Exception
{
    public ScoreBatchFailedException(string message, LlmResult lastResult, string prompt)
        : base(message)
    {
        LastResult = lastResult;
        Prompt = prompt;
    }
    public LlmResult LastResult { get; }
    public string Prompt { get; }
}
