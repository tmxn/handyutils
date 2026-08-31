using System.Collections.Concurrent;

namespace PlotBridge.Server;

/// <summary>
/// Watches a drop folder and ingests any text file written into it. This is the
/// zero-tooling path: from the Visual Studio Immediate Window (or any script, or
/// a shell redirect) write a file and the plot updates.
///
/// Asynchronous by nature, so a writer cannot tell when - or whether - its file
/// was picked up. A caller that needs to know should write the file and POST
/// /ingest instead, which does not answer until the data is in the store.
///
/// Destination comes from the filename; see <see cref="Ingest.FromFileName"/>.
/// </summary>
public sealed class DropWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<PushRequest, Task> _push;
    private readonly ILogger _log;

    public string Folder { get; }

    public DropWatcher(string dataDir, Func<PushRequest, Task> push, ILogger log)
    {
        _push = push;
        _log = log;
        Folder = Path.Combine(dataDir, "drop");
        Directory.CreateDirectory(Folder);

        _fsw = new FileSystemWatcher(Folder)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _fsw.Created += (_, e) => Handle(e.FullPath);
        _fsw.Changed += (_, e) => Handle(e.FullPath);
        _fsw.Renamed += (_, e) => Handle(e.FullPath);
    }

    private void Handle(string path)
    {
        // A single WriteAllText can raise several events; collapse them.
        var now = DateTime.UtcNow;
        if (_lastSeen.TryGetValue(path, out var prev) && (now - prev).TotalMilliseconds < 250) return;
        _lastSeen[path] = now;

        _ = Task.Run(async () =>
        {
            var text = await Ingest.ReadWhenReadableAsync(path);
            if (text is null) return;

            var (board, chart, series) = Ingest.FromFileName(path);

            try
            {
                await _push(new PushRequest
                {
                    Board = board,
                    Chart = chart,
                    Series = series,
                    Text = text,
                    Meta = new Dictionary<string, string> { ["source"] = "drop", ["file"] = Path.GetFileName(path) },
                });
                _log.LogInformation("Ingested {File} -> {Board}/{Chart}/{Series}", Path.GetFileName(path), board, chart, series);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Drop ingest failed for {File}: {Message}", path, ex.Message);
            }
        });
    }

    public void Dispose() => _fsw.Dispose();
}
