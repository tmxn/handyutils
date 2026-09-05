namespace HeadlessGpuKeeper;

/// <summary>
/// Watches the folders that version-stamped apps install into and re-pins them the
/// moment an updater drops a new version directory.
///
/// This is the whole reason the work lives in the keeper rather than in the GUI: the
/// preference is read by Windows when a process starts, so the entry has to be correct
/// before the app next launches. Reacting to the folder appearing gets us there in
/// seconds; the periodic sweep is only a backstop for events we missed while asleep.
/// </summary>
public sealed class DynamicPinWatcher : IDisposable
{
    const int DebounceMs = 2000;
    const int SweepIntervalMs = 15 * 60 * 1000;

    readonly List<FileSystemWatcher> _watchers = new();
    readonly System.Threading.Timer _debounce;
    readonly System.Threading.Timer _sweep;
    readonly object _gate = new();

    FileSystemWatcher? _configWatcher;
    bool _disposed;

    public PinRuleSet Rules { get; private set; } = new();

    /// <summary>Raised after any sync that actually changed the registry.</summary>
    public event Action<SyncReport>? Changed;

    public DynamicPinWatcher()
    {
        _debounce = new System.Threading.Timer(_ => RunSync(), null, Timeout.Infinite, Timeout.Infinite);
        _sweep = new System.Threading.Timer(_ => RunSync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Reload();
        _sweep.Change(SweepIntervalMs, SweepIntervalMs);
        WatchConfigFile();
    }

    /// <summary>Re-reads rules.json, rebuilds the watchers and syncs immediately.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            Rules = PinRuleSet.Load();
            RebuildWatchers();
        }

        RunSync();
    }

    /// <summary>Runs a sweep now. Safe to call from any thread.</summary>
    public SyncReport SyncNow()
    {
        PinRuleSet snapshot;
        lock (_gate) snapshot = Rules;

        SyncReport report = RePinner.Sync(snapshot);
        if (report.Changed) Changed?.Invoke(report);
        return report;
    }

    void RunSync()
    {
        try { SyncNow(); } catch { /* never let a sweep kill the keeper */ }
    }

    void RebuildWatchers()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            try { watcher.Dispose(); } catch { }
        }
        _watchers.Clear();

        // Several rules can share a parent (two apps under %LOCALAPPDATA%, say): one
        // watcher per distinct directory is enough, and the sweep is global anyway.
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (PinRule rule in Rules.Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Filter)) continue;

            string root = NearestExistingDirectory(rule.WatchRoot);
            if (string.IsNullOrEmpty(root)) continue;

            // A wildcard in the filename means new *files* matter; otherwise only the
            // appearance of a new version *directory* does, which is far quieter.
            bool wildFilename = Path.GetFileName(rule.ExpandedFilter) is var name
                && (name.Contains('*') || name.Contains('?'));

            roots[root] = roots.TryGetValue(root, out bool existing) ? existing || wildFilename : wildFilename;
        }

        foreach ((string root, bool watchFiles) in roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    NotifyFilter = watchFiles
                        ? NotifyFilters.DirectoryName | NotifyFilters.FileName
                        : NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                // A dropped watcher (network blip, folder replaced) would otherwise go
                // unnoticed until the next sweep; rebuilding restores it promptly.
                watcher.Error += (_, _) => ScheduleDebouncedSync();

                _watchers.Add(watcher);
            }
            catch
            {
                // Unwatchable path: the periodic sweep still covers this rule.
            }
        }
    }

    void WatchConfigFile()
    {
        try
        {
            string directory = Path.GetDirectoryName(PinRuleSet.ConfigPath)!;
            Directory.CreateDirectory(directory);

            _configWatcher = new FileSystemWatcher(directory, Path.GetFileName(PinRuleSet.ConfigPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            // The tray menu points people at this file to edit by hand, so pick edits up
            // without making them restart the keeper.
            _configWatcher.Changed += (_, _) => ScheduleDebouncedReload();
            _configWatcher.Created += (_, _) => ScheduleDebouncedReload();
            _configWatcher.Renamed += (_, _) => ScheduleDebouncedReload();
        }
        catch { }
    }

    void OnFileSystemEvent(object sender, FileSystemEventArgs e) => ScheduleDebouncedSync();

    /// <summary>
    /// An install writes many entries in a burst; collapse them into one sync that runs
    /// once the folder has settled.
    /// </summary>
    void ScheduleDebouncedSync()
    {
        if (_disposed) return;
        try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch { }
    }

    void ScheduleDebouncedReload()
    {
        if (_disposed) return;
        // Reload is idempotent and cheap; route it through the same debounce so a text
        // editor's multi-write save does not trigger a rebuild per write.
        try
        {
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            _ = Task.Delay(DebounceMs).ContinueWith(_ => { if (!_disposed) Reload(); });
        }
        catch { }
    }

    /// <summary>
    /// Walks up until a directory that exists is found. An app that is not installed yet
    /// still gets covered: we watch its nearest existing ancestor.
    /// </summary>
    static string NearestExistingDirectory(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current)) return current;
            current = Path.GetDirectoryName(current);
        }
        return "";
    }

    public void Dispose()
    {
        _disposed = true;

        foreach (FileSystemWatcher watcher in _watchers)
        {
            try { watcher.Dispose(); } catch { }
        }
        _watchers.Clear();

        try { _configWatcher?.Dispose(); } catch { }
        _debounce.Dispose();
        _sweep.Dispose();
    }
}
