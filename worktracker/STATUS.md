# WorkTracker — Build Status & Pending Issues

_Last updated: 2026-08-17, session: control theming + re-score day._

## Status: Milestones 1–4 done, running end-to-end

- WPF app (`src/WorkTracker`, net9.0-windows — .NET 9 SDK is what's installed; spec said 8)
- Live-verified: Pass 1 scoring and Pass 2 week narrative via `pi --no-session --print --model github-copilot/gpt-5.6-luna`
- Data dir: `%USERPROFILE%\WorkTrackerData\` (config pre-seeded with this repo + developer "timur")
- Test harness: `tests/wtcheck` (console; args: `<repo> [score] [interpret]`)

## Key implementation notes (deviations/decisions vs SPEC)

- `anchors/anchors.json` is a **placeholder with synthetic diffs** (M5 not done — scores are uncalibrated until real anchors are baked from this repo).
- `ScoreService.PromptVersion = 5` (bumped for diff triage); InterpretService still 4; `anchorVersion = 1`.
- **Diff triage** (new, in `ScoreService`): commits touching ≥30 files or ≥2000 lines across ≥5 files get a separate cheap LLM pre-check (file list + 3 KB diff sample only) asking whether the change looks like a merge/mechanical/generated change. Commits judged mechanical are scored from file list + 2 KB sample + triage reason — their full 15 KB diff is withheld from the scoring prompt. Verdict stored on `ScoreEntry.Triage`, shown as `[mechanical: …]` tag in the day view. Triage failure/timeout falls back to full diffs; it never blocks scoring. Live-verified via wtcheck (real commit triaged non-mechanical, scored with full diff).
- **ASCII hardening** (user request): prompts instruct plain ASCII; `Services/TextSanitizer.ToAscii` normalizes curly quotes/dashes/ellipses at parse time in both ScoreService and InterpretService, so cached data stays clean.
- LLM command resolution: PATH search prefers `.exe/.cmd/.bat` over extensionless files (npm POSIX shims crash CreateProcess — this was a real bug found via wtcheck).
- UI (user-requested round 1): maximized window, 40px cells, blue underlined clickable week headers, larger day/week view fonts.
- Window no longer starts maximized (user tweak): 1400×900, centered.
- **Thinking effort** (user request): `LlmSettings.ThinkingEffort` (default `medium`), editable in Settings (off/minimal/low/medium/high/xhigh/max). `LlmRunner.EffectiveArgs` strips any user `--thinking` from the args box and injects `--thinking <level>` when the resolved command is pi (basename `pi` or `pi.*`); non-pi commands are untouched. Recorded in `raw/` and the app log.
- **Dark theme + auto-switch** (user request): `Services/Theme.cs` swaps `Themes/Light.xaml` / `Themes/Dark.xaml` (GitHub-dark palette); mode `auto` follows the Windows `AppsUseLightTheme` registry setting live (via `SystemEvents.UserPreferenceChanged`). Config `Theme` (auto/light/dark) in Settings. All chrome moved to `DynamicResource` theme brushes; code-built views read `UiPalette` from the theme and `MainWindow` rebuilds the grid on `Theme.Changed`. The Load0-5 green ramp, badge greens, revert-red dot and black hatch work on both themes.
- **App log** (new): `Services/AppLog.cs` → `%USERPROFILE%\WorkTrackerData\log\worktracker.log`, thread-safe, self-trimming (keeps tail past ~1 MB), never throws. Covers: startup + config, every git invocation (args, exit, duration, output size) and timeouts, every LLM call (command, duration, exit, sizes) in addition to `raw/`, triage candidates + per-commit verdicts, batch prompt sizes and resulting scores, diff truncations, report cache hit/generate, and all UI error paths (batch failures, collection failures, dispatcher/AppDomain/unobserved-task exceptions). Use it to diagnose real-repo (giga-commit) runs.
- Score cache header (`promptVersion`/`anchorVersion`) is stamped with current versions only when the cache was empty at scoring-session start (fresh generation). Mixed generations keep the old header → "older prompt" banner stays until explicit re-score.
- Git author-date bucketing keeps commits with `AuthorDate >= windowStart-1d+12h` (loose guard; the `--since=windowStart-1d` git flag does the real cutoff).
- Day view loads diffs in background, capped at 25 commits/day.

## Bug fixes since last status

- **Text selection in the right pane** (user request): WPF `TextBlock` is not user-selectable. Day view (subject, comment, stat, diff) and week view (summary, notable, signal descriptions, alternatives, questions) now use borderless read-only `TextBox` (helper: `Selectable.Text`, style `SelectableText` in DayView.xaml; auto-grow via `MaxHeight=0`). Signal evidence hashes remain clickable Hyperlinks, moved to their own line below the description.

- **Missing "very high" grid cell (real-repo finding)**: App.xaml only defined `Load0..Load4` but SPEC §10 has 6 steps. Days with load ≥35 computed step 5, `FindBrush("Load5")` silently returned null → `Background = null` → cell invisible and unclickable (mouse passes through). Added `Load5` (#1A5532) and made `FindBrush` fall back to a neutral brush + log a warning instead of returning null.

## Latest additions

- **Re-score day** (user request): Tools menu item. Tracks the last-opened day (`_selectedDay`); clears that day's scores for the selected developer and re-calls the LLM for just that day (diff fetch + ScoreDayAsync). Week report auto-regenerates via its inputHash staleness check.
- **Dark control theme** (user request): `Themes/Controls.xaml` — implicit styles for Button, TextBox, ComboBox, ListBox (+item hover/selection), Expander, Menu/MenuItem (themed submenu popup, so the Tools menu is visible in dark mode), StatusBar, ToolTip, and ScrollBar, all driven by DynamicResource theme brushes. Theme dicts (Light/Dark) gained control chrome brushes (input bg/border, selection, hover, scrollbar, etc.). The default-Windows look of buttons/lists/textboxes is gone under both themes. Light theme is unchanged in feel. `SelectableTextBox` stays borderless.

## Pending issues / planned work

### 1. Giga-diff sanity check (deferred — user asked to hold the 2MB test)
Per spec: per-commit diff truncation to 15,000 chars + `[truncated]` marker (done in `GitCollector.GetDiff`), and ≤15 commits/LLM call. wtcheck prints `diff length` per commit.
- The 2MB file-swap test in a scratch repo is still unrun; `/tmp/gigat` can be recreated.
- **Still worth adding (not in spec, cheap insurance):** a batch-level prompt size budget. Current worst case ≈ 15 × 15KB + anchors ≈ 240KB prompt. If the configured LLM has a smaller context, split the batch further when combined diff text exceeds a budget (e.g. 100KB), feeding earlier batches as same-day context (mechanism already exists for >15-commit days). Note: diff triage already removes the *many-file* end of this; a single giant file still relies on the 15KB per-commit truncation.

### 2. Milestone 5 — anchor baking
Pick real low/mid/high commits from the configured repo, review with user, replace synthetic anchors, bump `anchorVersion`. Then prompt tuning via `raw/` logs. This is the main thing that makes scores meaningful.

### 3. UI/UX rough edges
- `AutoScoreAsync` runs from `SelectionChanged` with `async void`; UI stays responsive during LLM awaits (good) but git collection on startup is synchronous (fine for 4-week window, but a 100k-commit repo would freeze startup — consider Task.Run + spinner).
- No "Cancel" for in-flight scoring runs.
- Week header double-newline label ("W-0\nAug 10") could be two TextBlocks for better alignment.
- Day cell tooltip for unscored says "open day or re-open to score" — scoring actually happens automatically on developer select; tooltip wording is stale.
- No keyboard navigation; fine for MVP.

### 4. Correctness gaps vs spec §11
- "LLM command not found" preflight: currently surfaces as LlmResolveError dialog on first LLM action (spec-compliant), but a dedicated setup banner on first launch would be nicer.
- Cherry-picked duplicate scoring: accepted limitation, not surfaced anywhere in UI.
- No test for `isMerge` via ≥2-parents path (worktracker repo has no merges; wtcheck "(merge stat varies)" check is a no-op).
- `InterpretService.ComputeInputHash` uses full hashes (spec example says "hash:score pairs" — fine), but merge commits are excluded from the hash while included in the prompt context list; if a merge is added/removed in the week the report won't regenerate. Minor.

### 5. Housekeeping
- Decide: keep `tests/wtcheck` as permanent smoke harness — leaning yes (the `diff length` printout and triage threshold checks are now in it).
- `.gitignore` warnings about LF/CRLF are cosmetic; set `core.autocrlf` or add `.gitattributes`.
- Release story: `dotnet publish src/WorkTracker -c Release -r win-x64 --self-contained -o publish/WorkTracker` → 135 MB self-contained folder, runs without .NET installed; `publish/` is gitignored.

## Repo state
- git: `worktracker/` is its own repo, branch `main`.
- Uncommitted: diff-triage feature (ScoreService, Models, UiModels/DayView triage tag, wtcheck checks, SPEC §6, this file).
- Scratch: `/tmp/gigat` (giant-diff test repo), can delete.
