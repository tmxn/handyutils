# WorkTracker — Specification (MVP)

## 1. Purpose

A local C# desktop (WPF, .NET 8) app that shows a manager a 4-week activity grid
for a developer in a git repository. Day cells are colored by an LLM-assessed
**absolute effort score** (informed by diff content, not line counts). Clicking
reveals per-commit comments and per-week LLM narratives. The LLM is invoked
through a configurable external command (default: `pi`), whose provider
authentication is entirely outside the app.

Framing principle: the output is **review material with evidence and
hypotheses**, not automated performance verdicts. No goal-based evaluation,
no numeric "slack score", no cross-developer comparison in MVP.

## 2. Scope

### In (MVP)
- One repository (path configured), multiple developers discovered from git history.
- Developer selector; UI focuses on the selected developer only.
- 4-week window ending today (current week shown partial).
- Pass 1: absolute per-commit LLM scoring, anchored, cached by commit hash.
- Pass 2: per-week LLM narrative (within-developer comparison allowed), cached.
- Contribution-style grid (4 columns × 7 rows) colored by absolute day load.
- Day panel: commit list with scores, comments, expandable diffs.
- Week panel: LLM narrative.
- LLM command configurable; `pi --no-session --print` is the default.
- JSON caches on disk; incremental scoring of only new commits.
- Raw LLM response log for debugging prompt iteration.

### Out (post-MVP)
- Cross-developer comparison views.
- Goal-aware evaluation (what the developer was supposed to be doing).
- Multiple repositories, team baselines, trend reports beyond 4 weeks.
- Scheduling/automation, non-Windows support.

## 3. Architecture

```
┌─────────────────────────────────────────────────┐
│ WPF UI                                          │
│  toolbar (repo, developer, status)              │
│  4×7 grid (day cells)   │  detail panel         │
│                          │  (day view / week view)│
└──────────────┬──────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────┐
│ Services                                        │
│  ConfigStore      read/write config.json        │
│  GitCollector     spawn git, build CommitInfo[] │
│  ScoreService     Pass 1: batch, call LLM,      │
│                   validate, write score cache   │
│  InterpretService Pass 2: week narrative,       │
│                   cache with input hashing      │
│  LlmRunner        spawn configured command,     │
│                   stdin prompt → stdout JSON    │
│  Store            scores.json, reports/, raw/   │
└─────────────────────────────────────────────────┘
```

No embedded LLM calls, no HTTP from the app. The only process the app spawns
for AI work is the configured LLM command.

## 4. Data locations

```
worktracker/                  (source repo)
  SPEC.md
  anchors/anchors.json        (baked calibration anchors, part of the repo)
  src/WorkTracker/            (WPF project)

%USERPROFILE%\WorkTrackerData\   (runtime data)
  config.json
  scores.json
  reports/<developer>/<window>.json
  raw/                        (last 20 raw LLM responses, oldest pruned)
  log/worktracker.log         (diagnostic file log, self-trimming ~1 MB tail)
```

### config.json
```json
{
  "repoPath": "S:\\FastGitRoot\\appmain",
  "developers": [
    {
      "id": "timur",
      "displayName": "Timur Nuriyasov",
      "authorNames": ["Timur Nuriyasov"],
      "authorEmails": ["timur@example.com"]
    }
  ],
  "llm": {
    "command": "pi",
    "args": ["--no-session", "--print"],
    "timeoutSeconds": 600
  },
  "grid": {
    "loadThresholds": [0, 1, 10, 20, 35]
  }
}
```
- `authorNames` / `authorEmails`: matching is case-insensitive, exact or
  substring. A developer matches a commit if author name or email matches any
  pattern.
- `loadThresholds`: 5 cutoffs producing 6 color steps (see §10).
- `llm.command` is resolved via PATH; on Windows `pi.cmd` is checked when
  `pi` is not found.

## 5. Git collection

### Collection
- `git -C <repoPath> log --all --numstat --date=iso-strict`
  with `--format` yielding machine-parseable fields, bounded by
  `--since=<windowStart-1d> --until=<now>`.
- `--all` (matches existing `worklog.ps1` behavior; catches pre-merge branch
  work).
- Diffs are fetched in a second pass only for commits that need scoring
  (see §7): `git show --format= <hash>` per commit, truncated to 15,000 chars
  per commit with a `[truncated]` marker.
- Date bucketing uses **author date** in local timezone.

### CommitInfo (internal model)
```json
{
  "hash": "abc123…",
  "shortHash": "abc123",
  "authorName": "Timur Nuriyasov",
  "authorEmail": "timur@example.com",
  "authorDate": "2026-02-10T14:20:00+05:00",
  "subject": "Handle retry failure in payment worker",
  "body": "…(max 500 chars)…",
  "isMerge": false,
  "isRevert": false,
  "filesChanged": 5,
  "insertions": 84,
  "deletions": 21,
  "numstat": [["src/Payment/Worker.cs", 60, 12], "…"],
  "diff": "…(lazy, only when needed)…"
}
```
- `isRevert`: subject matches `/^Revert /i`.
- `isMerge`: `git log --merges` membership or ≥2 parents.
- Commits not matching any configured developer are kept for the developer
  picker's "unassigned authors" hint; they are never scored.

## 6. Pass 1 — absolute scoring

### Invariants
1. A commit's score is **absolute**: defined only by the baked anchors. It is
   never influenced by other commits in the window, by other developers, or by
   time. The same commit gets the same score forever (cache).
2. A developer doing trivial work consistently scores low every day — the
   grid must show *level*, not relative spikes.
3. Score input includes the diff content and the **other commits of the same
   day** (same developer) as context for difficulty inference. Same-day
   commits are context, never scale references.

### Anchor file: anchors/anchors.json
```json
{
  "anchorVersion": 1,
  "anchors": [
    {
      "id": "low",
      "targetScore": 2,
      "note": "Single-line config value change, no behavioral risk.",
      "diff": "…full embedded diff text…"
    },
    { "id": "mid", "targetScore": 5, "note": "…", "diff": "…" },
    { "id": "high", "targetScore": 9, "note": "…", "diff": "…" }
  ]
}
```
- Anchors are selected **during development** from the real repository and
  reviewed by the user. Diff content is embedded (not hashes) so anchors
  survive history rewrites.
- 3 anchors (low/mid/high); a mid anchor stabilizes the scale.
- `anchorVersion` is part of the score cache versioning (see §8).

### Scoring rules (embedded in prompt)
- Score = effort and complexity actually involved, not size. A one-line fix
  of a subtle concurrency bug scores higher than 300 lines of generated or
  boilerplate changes.
- Mechanical work (formatting, renames, dependency bumps) scores low.
- Reverts are real work (score the effort of the revert + diagnosis implied),
  but the prompt notes they should be flagged in the comment.
- Integers 1–10 only. When unsure, use the anchors to decide which side of a
  boundary the commit falls on.
- Every scored commit gets a one-sentence comment: what it does, plus a
  difficulty note when notable (e.g., "small diff but fixes a race condition").

### Diff triage (large commits)
- A commit is "large" when it touches many files (≥30) or many changed lines
  (≥2000 across ≥5 files). Before the scoring call, large commits get a cheap
  pre-check: one separate LLM call (per batch) asking, per commit, whether the
  change looks like a merge / mechanical / generated change (dependency or
  lockfile bumps, bulk renames, formatting sweeps, generated/vendored code)
  rather than real coding work.
- The triage call sees only the changed-file list (capped) and a short diff
  sample (~3 KB) — never the full diff. When unsure, it answers "not
  mechanical".
- Commits judged mechanical are scored from the file list + sample + triage
  reason only (no full diff in the scoring prompt); the verdict is stored on
  the score entry and shown in the day view. All other commits go to the
  scoring call with their full (truncated) diff as usual.
- Triage failures (bad output, timeout) fall back to "no triage" — full diffs
  are sent and scoring proceeds. Triage never blocks scoring.

### Batching
- One LLM call per (developer, day) with unscored commits.
- Days with >15 unscored commits are split; later batches receive the earlier
  batches' commit list (subject + stat + score) as same-day context.
- Only commits **missing from the score cache** are sent.

### Prompt shape (Pass 1)
```
You are scoring git commits on an absolute 1-10 effort scale.

SCALE — these reference commits define the scale. Score new commits relative
to them.
[anchor low, targetScore 2, diff]
[anchor mid, targetScore 5, diff]
[anchor high, targetScore 9, diff]

RULES: effort and complexity, not size; (scoring rules from §6);
integers 1-10; one-sentence comment per commit.

CONTEXT — same developer, same day (for difficulty inference only):
[day commit list: hash, subject, stat]

TASK — score these commits (include their diffs):
[per commit: hash, authorDate, subject, body, stat, diff]

Respond with ONLY valid JSON:
{"commits": [{"hash": "…", "score": 7, "comment": "…"}]}
```

### Validation & failure
- Parse stdout as JSON (tolerate and strip markdown fences; extract first
  balanced JSON object).
- Every requested hash must be present, score ∈ 1..10 integer, comment
  non-empty. Otherwise: retry once. Still failing: mark the batch failed,
  leave those commits unscored (cells render neutral), show a diagnostics
  dialog with the raw output, log to `raw/`.
- Never guess or backfill scores.

## 7. Pass 2 — week interpretation

- Trigger: user clicks a week cell/column (auto if not cached).
- Input: the week's commits (hash, date, subject, score, comment, stat —
  **no diffs**) plus the other weeks of the 4-day window aggregated per day
  (load + commit count) so the narrative may compare within the developer.
- Output: plain text with section markers (NOT JSON — models drop braces and
  break strict JSON validation more often than not). The prompt requires
  exactly these five sections, each marker on its own line:
```
##Summary
2-4 sentence activity summary.

##Notable
- items worth manager attention, each tied to commit hashes

##Signals
- [possible_blocker|possible_struggle|revert_loop|wip_chain|other] description. Evidence: hash1, hash2

##AlternativeExplanations
- …(meetings, debugging, blocked time…)…

##Questions
- …specific questions for a 1:1, evidence-linked…
```
  (`weekStart` is stamped by the app, not asked from the model.)
- Parsing (`NarrativeParser`): deliberately lenient — case-insensitive
  markers, stray colons, bullet-less/numbered/plain lines, prose preamble,
  missing sections (→ empty), "none" under Signals (→ no signals), unknown
  signal types (→ "other"), evidence extracted as 7-40 hex tokens after an
  "evidence:" marker (or simply absent). Hard errors: no recognizable section
  markers at all, or an empty ##Summary.
- Prompt constraints: no goal-based evaluation, no employment verdicts, no
  cross-developer statements (MVP). Every negative signal must carry evidence
  hashes and at least one alternative explanation.
- Rendering: plain text with simple section headers (no full markdown engine).

## 8. Caching (load-bearing)

### scores.json
```json
{
  "version": 1,
  "anchorVersion": 1,
  "promptVersion": 3,
  "entries": {
    "abc123…": {"score": 7, "comment": "…", "scoredAt": "…"}
  }
}
```
- Global across developers (scores are absolute; hashes are unique).
- Incremental: each load collects the window, diffs against cache, sends only
  missing hashes.
- An entry is **never silently re-scored**. Re-scoring is explicit:
  - "Re-score developer" — deletes that developer's entries, re-runs Pass 1.
  - "Re-score all" — clears entries, re-runs Pass 1 for the window.
- If the app's `promptVersion` or `anchorVersion` exceeds the file header,
  existing entries are kept but the app shows "scores generated with older
  prompt" and offers re-score. (Prompt iteration during development uses the
  explicit re-score path.)

### reports/<developer>/<windowStart>-<windowEnd>.json
```json
{
  "developer": "timur",
  "weekStart": "2026-02-09",
  "promptVersion": 3,
  "inputHash": "sha256:…",
  "generatedAt": "…",
  "report": { …Pass 2 output… }
}
```
- `inputHash` = SHA-256 over sorted `hash:score` pairs of the week's commits.
- Staleness: report regenerates automatically when `inputHash` changed
  (e.g., after a re-score) or `promptVersion` mismatched. Otherwise served
  from cache instantly, no LLM call.

### raw/
- Last 20 LLM responses (stdin prompt + stdout + stderr tail + exit code),
  named by timestamp. Oldest pruned. Used for prompt iteration and bug reports.

### Not cached
- Git metadata is re-collected every launch (`git log` over a 4-week window
  is cheap). No persistence of the commit list.

## 9. LLM runner

- Resolves `llm.command` via PATH (Windows: also check `<name>.cmd`).
- Spawns with `llm.args`, prompt on stdin, captures stdout/stderr.
- Preflight on first use: check the command resolves; if not, show a clear
  setup error (no retry loop).
- Timeout per call (`timeoutSeconds`, default 600); on timeout: kill, log,
  surface error.
- No authentication, API keys, or model selection in the app. Provider/model
  are the user's concern in their pi (or other CLI) configuration.

## 10. UI

### Main window
```
┌────────────────────────────────────────────────────────────┐
│ [repo: S:\FastGitRoot\appmain ▾] [developer: Timur ▾]      │
│ window: 2026-01-19 → 2026-02-15 (4 weeks)        [settings]│
├──────────────────────────────────┬─────────────────────────┤
│          W-3   W-2   W-1   W0    │                         │
│  Mon ██    ██    ░░    █░        │   detail panel          │
│  Tue █░    ░░    ██    ██        │   (day view or week     │
│  Wed ░░    ██    █░    ░░        │    view, see below)     │
│  …                              │                         │
├──────────────────────────────────┴─────────────────────────┤
│ status: collected 132 commits · 7 new scored · report cached│
└────────────────────────────────────────────────────────────┘
```
- Grid: 4 week-columns × 7 day-rows, ~26px cells, small gaps, weekday labels.
- Cell fill: color step from day load (sum of that day's Pass-1 scores).
- Cell markers: small dot if the day contains ≥1 revert (objective, no LLM).
- Unscored commits: cell renders neutral hatched step; tooltip explains.
- Tooltip (hover): date, load, N commits, top commit subject.
- Click day → day view. Click week header → week view.
- Status bar: collection/score/interpret progress, LLM errors, cache state.

### Day view
- Sorted commit list: time, subject, score badge (colored 1–10), comment,
  files/insertions/deletions, expandable diff (read-only).
- Merges listed, visually de-emphasized, not scored.

### Week view
- Week summary text (Pass 2), then Notable, Signals (with evidence hashes
  clickable → jump to commit in day view), Alternative explanations,
  Questions.
- "Regenerate report" button (bypasses cache staleness; explicit).

### Settings
- Repo path, developer identities (names/emails, multiple per developer),
  LLM command/args/timeout, load thresholds.
- "Unassigned authors" list: authors found in the window who match no
  configured developer, with a button to add them.

### Color scale
- 6 steps from `loadThresholds` `[0, 1, 10, 20, 35]`:
  - 0: empty (neutral background)
  - 1–9: low (pale)
  - 10–19: moderate
  - 20–34: high
  - 35+: very high
- Palette: GitHub-style green ramp; thresholds are config constants to tune
  empirically after first real runs.

## 11. Edge cases

| Case | Behavior |
|---|---|
| No commits in window | Empty grid, informational message, no LLM calls |
| LLM command not found | Setup error on first LLM action; grid still works from cache |
| Malformed LLM JSON | 1 retry, then batch failed; diagnostics dialog; raw logged |
| LLM timeout | Kill process, error surfaced, batch left unscored |
| Author identity change (new email) | Author appears in "unassigned authors" |
| Cherry-picked duplicate (new hash) | Scored again (accepted limitation, documented) |
| Day with only merges | Neutral cell, tooltip "merges only" |
| Repo path invalid / not a git repo | Blocking error at startup with the exact message from git |
| Window slides past a scored week | Nothing re-scores (hashes unchanged) |

## 12. Milestones

1. **Skeleton** — WPF project, config load/save, data dir bootstrap, settings UI.
2. **Git layer** — GitCollector, developer picker (incl. unassigned authors),
   raw commit list visible in day view. No LLM.
3. **Pass 1** — LlmRunner, ScoreService, anchors.json placeholder, scores.json,
   grid colored by absolute day load, re-score commands.
4. **Pass 2** — InterpretService, week view, report cache with inputHash.
5. **Anchor baking** — select real low/mid/high commits from the configured
   repo, review with user, finalize anchors.json; prompt tuning via raw logs.
6. **Hardening** — edge cases from §11, status bar polish, diagnostics UX.

## 13. Deliberate non-goals (repeated for emphasis)

- No "slack score", no numeric performance rating, no verdict language.
- No goal/target input in MVP.
- No cross-developer comparison in MVP (possible later over the same caches).
- No source-code content leaving the machine except via the user's own LLM
  command (that transfer is the app's essence, and the command is user-chosen).
