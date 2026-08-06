using System.Collections.Concurrent;

namespace PlotBridge.Server;

/// <summary>
/// Watches a drop folder and ingests any text file written into it. This is the
/// zero-tooling path: from the Visual Studio Immediate Window (or any script, or
/// a shell redirect) write a file and the plot updates.
///
/// Filename decides the destination, split on "__":
///   <c>pts.tsv</c>                     -> board "default", chart "main",  series "pts"
///   <c>hull__pts.tsv</c>               -> board "default", chart "hull",  series "pts"
///   <c>run2__hull__pts.tsv</c>         -> board "run2",    chart "hull",  series "pts"
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
            var text = await ReadWhenReadableAsync(path);
            if (text is null) return;

            var stem = Path.GetFileNameWithoutExtension(path);
            var parts = stem.Split("__", StringSplitOptions.RemoveEmptyEntries);
            var (board, chart, series) = parts.Length switch
            {
                >= 3 => (parts[0], parts[1], string.Join("__", parts.Skip(2))),
                2 => ("default", parts[0], parts[1]),
                _ => ("default", "main", stem),
            };

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

    /// <summary>The writer may still hold the file; retry briefly before giving up.</summary>
    private static async Task<string?> ReadWhenReadableAsync(string path)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync();
            }
            catch (IOException)
            {
                await Task.Delay(60);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(60);
            }
        }
        return null;
    }

    public void Dispose() => _fsw.Dispose();
}
