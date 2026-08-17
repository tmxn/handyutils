using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WorkTracker.Services;

namespace WorkTracker;

public partial class MainWindow : Window
{
    private const double CellSize = 40;

    private readonly ConfigStore _configStore;
    private AppConfig _config;
    private GitCollector? _git;
    private LlmRunner? _llm;
    private ScoreService? _scoreService;
    private InterpretService? _interpret;
    private AnchorSet? _anchors;
    private ScoreCache _scores = new();
    private GitCollectionResult? _collection;
    private Developer? _selectedDev;
    private DateTime? _selectedDay;
    private DateTime[] _weekStarts = Array.Empty<DateTime>();
    private bool _busy;
    private bool _initialized;

    public MainWindow(AppConfig config)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowChrome();
        Theme.Changed += OnThemeChanged;
        _config = config;
        _configStore = new ConfigStore();
        Loaded += async (_, _) =>
        {
            if (!_initialized)
            {
                _initialized = true;
                await InitializeAsync();
            }
        };
    }

    // ---------- initialization ----------

    private async Task InitializeAsync()
    {
        RepoText.Text = _config.RepoPath;
        SetStatus("initializing…");

        var gitError = GitCollector.ValidateRepo(_config.RepoPath);
        if (gitError != null)
        {
            AppLog.Error($"repo validation failed: {gitError}");
            MessageBox.Show(this,
                $"Repository '{_config.RepoPath}' is not usable:\n\n{gitError}\n\nOpen Settings to fix the repo path.",
                "WorkTracker — repository error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("repository invalid — open Settings");
            DeveloperCombo.IsEnabled = false;
            return;
        }

        try
        {
            _anchors = ScoreService.LoadAnchors();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WorkTracker — anchors", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _git = new GitCollector(_config.RepoPath);
        _llm = new LlmRunner(_config.Llm);
        _scoreService = new ScoreService(_llm);
        _interpret = new InterpretService(_llm);
        _scores = Store.LoadScores();

        try
        {
            _collection = _git.Collect();
        }
        catch (Exception ex)
        {
            AppLog.Error("git collection failed", ex);
            SetStatus("git collection failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "WorkTracker — git", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        WindowText.Text =
            $"window: {_collection.WindowStart:yyyy-MM-dd} → {DateTime.Now:yyyy-MM-dd} (4 weeks)";
        _weekStarts = new[]
        {
            _collection.WindowStart,
            _collection.WindowStart.AddDays(7),
            _collection.WindowStart.AddDays(14),
            _collection.WindowStart.AddDays(21),
        };

        PopulateDeveloperPicker();
        UpdatePromptStalenessStatus();

        if (_config.Developers.Count > 0)
        {
            DeveloperCombo.SelectedIndex = 0;
            // SelectionChanged fires BuildGrid + auto-scoring.
        }
        else
        {
            BuildGrid();
            SetStatus("no developers configured — add one in Settings");
        }
        await Task.CompletedTask;
    }

    private void PopulateDeveloperPicker()
    {
        DeveloperCombo.Items.Clear();
        foreach (var dev in _config.Developers)
        {
            var item = new ComboBoxItem { Content = dev.DisplayName, Tag = dev };
            DeveloperCombo.Items.Add(item);
        }
        var unassigned = UnassignedAuthors().Count;
        if (unassigned > 0)
        {
            var suffix = unassigned == 1 ? "" : "s";
            var hint = new ComboBoxItem
            {
                Content = $"({unassigned} unassigned author{suffix} — see Settings)",
                IsEnabled = false,
            };
            DeveloperCombo.Items.Add(hint);
        }
    }

    private List<(string Name, string Email)> UnassignedAuthors()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        if (_collection == null) return result;
        foreach (var c in _collection.Commits)
        {
            if (_config.Developers.Any(d => d.Matches(c.AuthorName, c.AuthorEmail))) continue;
            var key = c.AuthorName + "<" + c.AuthorEmail + ">";
            if (seen.Add(key)) result.Add((c.AuthorName, c.AuthorEmail));
        }
        return result;
    }

    private void UpdatePromptStalenessStatus()
    {
        if (_scores.Entries.Count == 0) return;
        if (_scores.PromptVersion < ScoreService.PromptVersion ||
            (_anchors != null && _scores.AnchorVersion < _anchors.AnchorVersion))
        {
            SetStatus($"scores generated with an older prompt/anchors ({_scores.PromptVersion}/{_scores.AnchorVersion}) — use Tools to re-score");
        }
    }

    // ---------- developer / grid ----------

    private async void DeveloperCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _collection == null) return;
        if (DeveloperCombo.SelectedItem is not ComboBoxItem { Tag: Developer dev })
        {
            _selectedDev = null;
            return;
        }
        _selectedDev = dev;
        BuildGrid();
        DetailHost.Content = null;
        await AutoScoreAsync();
    }

    private IEnumerable<CommitInfo> DevCommits()
    {
        if (_collection == null || _selectedDev == null) yield break;
        foreach (var c in _collection.Commits)
            if (_selectedDev.Matches(c.AuthorName, c.AuthorEmail))
                yield return c;
    }

    private void BuildGrid()
    {
        GridHost.Children.Clear();
        if (_weekStarts.Length == 0) return;

        GridHost.ColumnDefinitions.Clear();
        GridHost.RowDefinitions.Clear();
        GridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        for (int w = 0; w < 4; w++)
            GridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        GridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        for (int d = 0; d < 7; d++)
            GridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellSize + 6) });

        var weekdayNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        for (int w = 0; w < 4; w++)
        {
            var header = MakeWeekHeader(w, _weekStarts[w]);
            GridHost.Children.Add(header);
        }

        for (int d = 0; d < 7; d++)
        {
            var label = new TextBlock
            {
                Text = weekdayNames[d],
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetRow(label, d + 1);
            Grid.SetColumn(label, 0);
            GridHost.Children.Add(label);

            for (int w = 0; w < 4; w++)
            {
                var day = _weekStarts[w].AddDays(d);
                var cell = MakeDayCell(day, w, d);
                GridHost.Children.Add(cell);
            }
        }
    }

    private TextBlock MakeWeekHeader(int col, DateTime weekStart)
    {
        var tb = new TextBlock
        {
            Text = $"W-{3 - col}\n{weekStart:MMM d}",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            TextDecorations = TextDecorations.Underline,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = UiPalette.LinkBlue,
            Cursor = Cursors.Hand,
            ToolTip = "Click for the week report",
        };
        Grid.SetRow(tb, 0);
        Grid.SetColumn(tb, col + 1);
        tb.MouseLeftButtonUp += (_, _) => ShowWeekViewAsync(weekStart);
        return tb;
    }

    private UIElement MakeDayCell(DateTime day, int col, int row)
    {
        var dayCommits = DevCommits()
            .Where(c => c.AuthorDate.Date == day.Date)
            .ToList();

        var nonMerge = dayCommits.Where(c => !c.IsMerge).ToList();
        var scored = nonMerge.Where(c => _scores.Entries.ContainsKey(c.Hash)).ToList();
        var unscored = nonMerge.Count - scored.Count;
        var load = scored.Sum(c => _scores.Entries[c.Hash].Score);
        var hasRevert = dayCommits.Any(c => c.IsRevert);

        (Brush fill, string tooltip) = ComputeCellFill(load, unscored, nonMerge.Count, dayCommits.Count);
        var top = scored
            .OrderByDescending(c => _scores.Entries[c.Hash].Score)
            .Select(c => c.Subject)
            .FirstOrDefault();

        var border = new Border
        {
            Width = CellSize,
            Height = CellSize,
            CornerRadius = new CornerRadius(3),
            Background = fill,
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            ToolTip = tooltip + (top != null ? $"\n{Truncate(top, 80)}" : ""),
        };

        var host = new Grid();
        host.Children.Add(border);
        if (hasRevert)
        {
            host.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(0xCB, 0x24, 0x31)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
            });
        }

        Grid.SetRow(host, row + 1);
        Grid.SetColumn(host, col + 1);
        host.MouseLeftButtonUp += (_, _) => ShowDayView(day);
        return host;
    }

    private (Brush, string) ComputeCellFill(int load, int unscored, int nonMergeCount, int totalCount)
    {
        if (nonMergeCount == 0)
            return (FindBrush("Load0"), "merges only");
        if (unscored > 0)
            return (FindBrush("UnscoredBrush"),
                $"{unscored} commit(s) unscored — open day or re-open to score");
        var t = _config.Grid.LoadThresholds;
        var step = load <= t[0] ? 0 : (int)t.Skip(1).TakeWhile(x => load >= x).Count() + 1;
        var tip = $"load {load}";
        return (FindBrush("Load" + step), tip);
    }

    private static readonly Brush FallbackBrush =
        new SolidColorBrush(Color.FromRgb(0xE1, 0xE4, 0xE8));

    private void OnThemeChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyWindowChrome();
            if (_collection != null && _selectedDev != null) BuildGrid();
        });
    }

    /// <summary>
    /// Win11: dark title bar + Mica backdrop (see Services/WindowChrome.cs).
    /// When Mica is available the window background must be transparent so the
    /// backdrop shows through; otherwise keep the solid theme color.
    /// </summary>
    private void ApplyWindowChrome()
    {
        var mica = WindowChrome.Apply(this, Theme.Current == "dark");
        Background = mica ? Brushes.Transparent : Theme.Brush("WindowBackground");
    }

    private Brush FindBrush(string key)
    {
        // Never return null: a null Background makes the cell invisible AND unclickable.
        if (Application.Current.Resources[key] is Brush b) return b;
        AppLog.Warn($"missing brush resource '{key}' — falling back to neutral");
        return FallbackBrush;
    }

    // ---------- Pass 1: auto scoring ----------

    private async Task AutoScoreAsync()
    {
        if (_selectedDev == null || _collection == null || _scoreService == null || _anchors == null)
            return;
        _busy = true;
        try
        {
            var byDay = DevCommits()
                .Where(c => !c.IsMerge && !_scores.Entries.ContainsKey(c.Hash))
                .GroupBy(c => c.AuthorDate.Date)
                .OrderBy(g => g.Key)
                .ToList();

            var total = byDay.Sum(g => g.Count());
            if (total == 0)
            {
                SetStatus($"{_collection.Commits.Count(c => _selectedDev!.Matches(c.AuthorName, c.AuthorEmail))} commits in window · all scored · {(LLMReady() ? "llm ready" : "llm command not found")}");
                return;
            }
            AppLog.Info($"auto-scoring {total} unscored commit(s) for {_selectedDev.DisplayName} across {byDay.Count} day(s)");

            // Was the cache empty at session start? If so the entries we are about to write
            // are a clean generation, so stamp the header with the current prompt/anchor versions.
            var cacheWasEmpty = _scores.Entries.Count == 0;
            var done = 0;
            foreach (var group in byDay)
            {
                var day = group.Key;
                var commits = group.ToList();
                SetStatus($"scoring {day:yyyy-MM-dd}: {commits.Count} commit(s)…");
                await ScoreDayAsync(_selectedDev, day, commits, cacheWasEmpty);
                done += commits.Count;
                BuildGrid();
            }
            AppLog.Info($"auto-scoring finished: {done} new commit(s) scored for {_selectedDev.DisplayName}");
            SetStatus($"scored {done} new commit(s) for {_selectedDev.DisplayName}");
        }
        catch (LlmResolveError ex)
        {
            AppLog.Error("LLM command not found", ex);
            MessageBox.Show(this, ex.Message, "WorkTracker — LLM setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("LLM command not found — grid shown from cache");
        }
        finally
        {
            _busy = false;
        }
    }

    private bool LLMReady() => _llm?.ResolveCommand() != null;

    private async Task ScoreDayAsync(Developer dev, DateTime day, List<CommitInfo> unscored, bool stampHeader)
    {
        if (_scoreService == null || _anchors == null || _git == null) return;

        // Fetch diffs for unscored commits (second pass, only what's needed) — off the UI thread.
        await Task.Run(() =>
        {
            foreach (var c in unscored) _git.GetDiff(c);
        });

        var batches = Chunk(unscored, ScoreService.MaxCommitsPerBatch).ToList();
        var sameDayContext = DevCommits()
            .Where(c => c.AuthorDate.Date == day && _scores.Entries.ContainsKey(c.Hash) &&
                        !unscored.Contains(c))
            .ToList();

        for (var i = 0; i < batches.Count; i++)
        {
            // Later batches receive the earlier batches' commit list as same-day context.
            var context = sameDayContext.Concat(
                batches.Take(i).SelectMany(b => b).Where(c => _scores.Entries.ContainsKey(c.Hash))).ToList();

            try
            {
                var batch = batches[i];
                var result = await _scoreService.ScoreBatchAsync(
                    batch, context, _anchors,
                    status: s => SetStatus($"{day:yyyy-MM-dd}: {s}"));

                foreach (var (hash, entry) in result.Scored)
                    _scores.Entries[hash] = entry;
                // Clean generation (cache was empty at session start): stamp current versions.
                // Mixed generations keep the old header so the "older prompt" banner stays
                // until an explicit re-score.
                if (stampHeader)
                {
                    _scores.PromptVersion = ScoreService.PromptVersion;
                    _scores.AnchorVersion = _anchors!.AnchorVersion;
                }
                Store.SaveScores(_scores);
            }
            catch (ScoreBatchFailedException ex)
            {
                AppLog.Error($"score batch failed for {day:yyyy-MM-dd}", ex);
                ShowDiagnostics(ex);
                // Leave this batch unscored; continue with the rest of the day/window.
            }
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    // ---------- day view ----------

    private void ShowDayView(DateTime day)
    {
        _selectedDay = day;
        var commits = DevCommits()
            .Where(c => c.AuthorDate.Date == day.Date)
            .OrderBy(c => c.AuthorDate)
            .ToList();
        var rows = commits.Select(c => new CommitRow
        {
            Commit = c,
            Score = _scores.Entries.TryGetValue(c.Hash, out var e) && !c.IsMerge ? e : null,
        }).ToList();

        var view = new DayView();
        view.Show(day, rows);
        DetailHost.Content = view;

        // Load diffs in the background (lazy second pass), capped to keep the UI snappy.
        var toLoad = rows.Where(r => !r.IsMerge && _git != null).Take(25).ToList();
        if (toLoad.Count > 0)
            _ = Task.Run(() =>
            {
                foreach (var r in toLoad)
                {
                    var diff = _git!.GetDiff(r.Commit) ?? "(no diff)";
                    Dispatcher.BeginInvoke(() => r.DiffText = diff);
                }
            });
    }

    // ---------- week view ----------

    private async void ShowWeekViewAsync(DateTime weekStart)
    {
        if (_busy || _selectedDev == null || _interpret == null) return;
        _busy = true;
        SetStatus($"week {weekStart:yyyy-MM-dd}: loading report…");
        try
        {
            var weekCommits = DevCommits()
                .Where(c => c.AuthorDate >= weekStart && c.AuthorDate < weekStart.AddDays(7))
                .ToList();
            var report = await _interpret.GetWeekReportAsync(
                _selectedDev.Id, weekStart, weekCommits, DevCommits().ToList(), _scores, false,
                status: SetStatus);
            RenderWeekView(weekStart, report, weekCommits);
        }
        catch (LlmResolveError ex)
        {
            MessageBox.Show(this, ex.Message, "WorkTracker — LLM setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("LLM command not found");
        }
        catch (ScoreBatchFailedException ex)
        {
            ShowDiagnostics(ex);
        }
        catch (Exception ex)
        {
            AppLog.Error("week report failed", ex);
            MessageBox.Show(this, ex.Message, "WorkTracker — week report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void RegenerateWeekReport_Click(object sender, RoutedEventArgs e)
    {
        var weekStart = ((Button)sender).Tag as DateTime?;
        if (weekStart == null || _busy || _selectedDev == null || _interpret == null) return;
        _busy = true;
        SetStatus($"week {weekStart:yyyy-MM-dd}: regenerating report…");
        try
        {
            var weekCommits = DevCommits()
                .Where(c => c.AuthorDate >= weekStart.Value && c.AuthorDate < weekStart.Value.AddDays(7))
                .ToList();
            var report = await _interpret.GetWeekReportAsync(
                _selectedDev.Id, weekStart.Value, weekCommits, DevCommits().ToList(), _scores, true,
                status: SetStatus);
            RenderWeekView(weekStart.Value, report, weekCommits);
        }
        catch (ScoreBatchFailedException ex)
        {
            ShowDiagnostics(ex);
        }
        catch (Exception ex)
        {
            AppLog.Error("week report regeneration failed", ex);
            MessageBox.Show(this, ex.Message, "WorkTracker — week report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderWeekView(DateTime weekStart, ReportFile report, List<CommitInfo> weekCommits)
    {
        var n = report.Report;
        var panel = new StackPanel { Margin = new Thickness(12) };
        void AddHeader(string text)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Margin = new Thickness(0, 14, 0, 4),
            });
        }
        void AddBody(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Selectable (borderless read-only TextBox) so managers can copy the text.
            panel.Children.Add(Selectable.Text(text, 14, UiPalette.DarkText, new Thickness(0, 0, 0, 5)));
        }

        panel.Children.Add(new TextBlock
        {
            Text = $"Week of {weekStart:yyyy-MM-dd}",
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 2),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"generated {report.GeneratedAt:yyyy-MM-dd HH:mm} | {weekCommits.Count} commits",
            Foreground = UiPalette.MutedText,
            FontSize = 12,
        });

        AddHeader("Summary");
        AddBody(n.Summary);

        AddHeader("Notable");
        foreach (var item in n.Notable) AddBody("• " + item);

        if (n.Signals.Count > 0)
        {
            AddHeader("Signals (hypotheses, not verdicts)");
            foreach (var s in n.Signals)
            {
                // Description in a selectable box; evidence hashes stay as clickable links below it.
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
                sp.Children.Add(Selectable.Text($"• [{s.Type}] {s.Description}", 14, UiPalette.DarkText));
                if (s.Evidence.Count > 0)
                {
                    var links = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(16, 2, 0, 0),
                    };
                    foreach (var hash in s.Evidence)
                    {
                        var h = hash;
                        var link = new TextBlock { Margin = new Thickness(0, 0, 10, 0) };
                        var hl = new Hyperlink(new Run(h.Substring(0, Math.Min(8, h.Length))))
                        {
                            Foreground = UiPalette.LinkBlue,
                        };
                        hl.Click += (_, _) => JumpToCommit(h);
                        link.Inlines.Add(hl);
                        links.Children.Add(link);
                    }
                    sp.Children.Add(links);
                }
                panel.Children.Add(sp);
            }
        }

        AddHeader("Alternative explanations");
        foreach (var a in n.AlternativeExplanations) AddBody("• " + a);

        AddHeader("Questions for 1:1");
        foreach (var q in n.Questions) AddBody("• " + q);

        var regen = new Button
        {
            Content = "Regenerate report",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = weekStart,
        };
        regen.Click += RegenerateWeekReport_Click;
        panel.Children.Add(regen);

        DetailHost.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
        SetStatus($"week {weekStart:yyyy-MM-dd}: report ready");
    }

    private void JumpToCommit(string hash)
    {
        var commit = DevCommits().FirstOrDefault(c => c.Hash.StartsWith(hash, StringComparison.OrdinalIgnoreCase));
        if (commit == null)
        {
            MessageBox.Show(this, "Commit not found in current window.", "WorkTracker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowDayView(commit.AuthorDate.Date);
    }

    // ---------- re-score / settings / misc ----------

    private async void RescoreDay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDev == null || _busy || _selectedDay == null)
        {
            MessageBox.Show(this, _selectedDay == null
                    ? "Open a day first (click its cell), then use Re-score day."
                    : "No developer selected.",
                "WorkTracker", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var day = _selectedDay.Value;
        var dayCommits = DevCommits()
            .Where(c => c.AuthorDate.Date == day.Date && !c.IsMerge)
            .ToList();
        if (dayCommits.Count == 0)
        {
            MessageBox.Show(this, $"No non-merge commits for {day:yyyy-MM-dd}.", "WorkTracker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Re-score all {dayCommits.Count} commit(s) for {day:yyyy-MM-dd} ({_selectedDev.DisplayName})? " +
                "This re-calls the LLM for that day.",
                "WorkTracker", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        foreach (var c in dayCommits) _scores.Entries.Remove(c.Hash);
        Store.SaveScores(_scores);
        BuildGrid();

        _busy = true;
        try
        {
            SetStatus($"re-scoring {day:yyyy-MM-dd}: {dayCommits.Count} commit(s)…");
            await ScoreDayAsync(_selectedDev, day, dayCommits, false);
            BuildGrid();
            SetStatus($"re-scored {day:yyyy-MM-dd}: {dayCommits.Count} commit(s)");
        }
        catch (LlmResolveError ex)
        {
            AppLog.Error("LLM command not found during day re-score", ex);
            MessageBox.Show(this, ex.Message, "WorkTracker — LLM setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("LLM command not found");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void RescoreDeveloper_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDev == null || _busy) return;
        if (MessageBox.Show(this, $"Re-score all window commits for {_selectedDev.DisplayName}? " +
                                  "This deletes that developer's cached scores and calls the LLM again.",
                "WorkTracker", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _scores.Entries = _scores.Entries
            .Where(kv => !DevCommits().Any(c => c.Hash == kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Store.SaveScores(_scores);
        BuildGrid();
        await AutoScoreAsync();
    }

    private async void RescoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (MessageBox.Show(this, "Clear ALL cached scores and re-score the 4-week window? " +
                                  "This makes one LLM call per day with unscored commits.",
                "WorkTracker", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _scores.Entries.Clear();
        Store.SaveScores(_scores);
        BuildGrid();
        await AutoScoreAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_config, UnassignedAuthors()) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _configStore.Save(_config);
            MessageBox.Show(this, "Settings saved. Restarting…", "WorkTracker",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RestartApp();
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer", ConfigStore.DataDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        Process.Start(new ProcessStartInfo(exe!) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    // ---------- diagnostics & status ----------

    private void ShowDiagnostics(ScoreBatchFailedException ex)
    {
        AppLog.Error($"LLM batch failed: {ex.Message} (exit={ex.LastResult.ExitCode}, " +
                     $"timeout={ex.LastResult.TimedOut})");
        var raw = $"exit code: {ex.LastResult.ExitCode}\ntimed out: {ex.LastResult.TimedOut}\n\n" +
                  $"--- stdout (tail) ---\n{Tail(ex.LastResult.Stdout, 3000)}\n\n" +
                  $"--- stderr (tail) ---\n{Tail(ex.LastResult.Stderr, 1500)}";
        MessageBox.Show(this,
            ex.Message + "\n\nRaw LLM exchange (also logged to raw/):\n\n" + raw,
            "WorkTracker — LLM diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        SetStatus("LLM batch failed — see diagnostics");
    }

    private static string Tail(string s, int n) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= n ? s : "…" + s[^n..]);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private void SetStatus(string text)
    {
        Dispatcher.BeginInvoke(() => StatusText.Text = text);
    }
}
