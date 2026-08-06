using System.Text.Json;

namespace PlotBridge.Server;

/// <summary>
/// In-memory board state with a debounced JSON snapshot on disk, so a page
/// reopened after a Visual Studio restart comes back with its plots, styles and
/// chart options intact.
/// </summary>
public sealed class Store
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Board> _boards = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dir;
    private readonly ILogger _log;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Store(string dataDir, ILogger log)
    {
        _log = log;
        _dir = Path.Combine(dataDir, "boards");
        Directory.CreateDirectory(_dir);
        LoadAll();
    }

    private void LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var b = JsonSerializer.Deserialize<Board>(File.ReadAllText(file), Json);
                if (b is not null && !string.IsNullOrWhiteSpace(b.Name)) _boards[b.Name] = b;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not load board snapshot {File}: {Message}", file, ex.Message);
            }
        }
        if (_boards.Count > 0) _log.LogInformation("Restored {Count} board(s) from {Dir}", _boards.Count, _dir);
    }

    /// <summary>Runs <paramref name="work"/> under the store lock and marks the
    /// board dirty. Returns whatever the callback produces.</summary>
    public T Mutate<T>(string boardName, Func<Board, T> work)
    {
        lock (_gate)
        {
            var board = GetOrAddLocked(boardName);
            var result = work(board);
            _dirty.Add(board.Name);
            return result;
        }
    }

    public Board Snapshot(string boardName)
    {
        lock (_gate)
        {
            // Round-trip through JSON so callers can never mutate live state.
            var board = GetOrAddLocked(boardName);
            return JsonSerializer.Deserialize<Board>(JsonSerializer.Serialize(board, Json), Json)!;
        }
    }

    public string[] BoardNames()
    {
        lock (_gate) return _boards.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Board GetOrAddLocked(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "default";
        if (!_boards.TryGetValue(name, out var b))
        {
            b = new Board { Name = name };
            _boards[name] = b;
        }
        return b;
    }

    public static Chart GetOrAddChart(Board board, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "main" : name.Trim();
        var chart = board.Charts.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (chart is null)
        {
            chart = new Chart { Name = name };
            board.Charts.Add(chart);
        }
        return chart;
    }

    /// <summary>Lowest palette slot not already taken in this chart, so a new
    /// series never repaints an existing one. Colour follows the entity.</summary>
    public static int NextSlot(Chart chart)
    {
        var taken = chart.Series.Where(s => s.Style.Slot.HasValue).Select(s => s.Style.Slot!.Value).ToHashSet();
        for (var i = 0; ; i++) if (!taken.Contains(i)) return i;
    }

    // ---- persistence -------------------------------------------------------

    public async Task RunSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(750, ct); } catch (OperationCanceledException) { break; }
            FlushPending();
        }
        FlushPending();
    }

    public void FlushPending()
    {
        (string Name, string Json)[] pending;
        lock (_gate)
        {
            if (_dirty.Count == 0) return;
            pending = _dirty
                .Where(_boards.ContainsKey)
                .Select(n => (n, JsonSerializer.Serialize(_boards[n], Json)))
                .ToArray();
            _dirty.Clear();
        }

        foreach (var (name, json) in pending)
        {
            try
            {
                var path = Path.Combine(_dir, Sanitize(name) + ".json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not save board {Board}: {Message}", name, ex.Message);
            }
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
