using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlotBridge.Server;

// The content root decides where wwwroot is found, and by default it is the
// *current directory* - so launching the exe from a shortcut, a script, or another
// process would serve /health fine and 404 the page. Pin it instead.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ResolveContentRoot(),
});

// Next to the exe is where publish puts wwwroot; the current directory is where it
// lives under `dotnet run`, which serves it from the project folder.
static string ResolveContentRoot()
{
    var beside = AppContext.BaseDirectory;
    if (Directory.Exists(Path.Combine(beside, "wwwroot"))) return beside;

    var cwd = Directory.GetCurrentDirectory();
    if (Directory.Exists(Path.Combine(cwd, "wwwroot"))) return cwd;

    return beside;
}

var port = 8777;
if (Environment.GetEnvironmentVariable("PLOTBRIDGE_PORT") is { Length: > 0 } envPort && int.TryParse(envPort, out var ep)) port = ep;
if (builder.Configuration["port"] is { Length: > 0 } argPort && int.TryParse(argPort, out var ap)) port = ap;

// Probe the port before starting the host. Letting Kestrel fail the bind works,
// but it buries the one useful line under a page of hosting stack trace, and
// "another instance is already running" is the single most likely startup problem.
if (!IsPortFree(port))
{
    Console.Error.WriteLine($"PlotBridge: port {port} is already in use - another instance is probably running.");
    Console.Error.WriteLine($"            Open http://localhost:{port}/ to check, or pass --port <n> to use a different one.");
    return 1;
}

static bool IsPortFree(int candidate)
{
    try
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}

builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var app = builder.Build();
var log = app.Logger;

var dataDir = Environment.GetEnvironmentVariable("PLOTBRIDGE_DATA")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlotBridge");
Directory.CreateDirectory(dataDir);

var store = new Store(dataDir, log);
var hub = new Hub(log);

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

// ---------------------------------------------------------------- push pipeline

async Task<(bool Ok, string Message, int Count)> ApplyPushAsync(PushRequest req)
{
    var (x, y, z, note) = Normalize(req);
    if (y.Length == 0) return (false, "no points parsed" + note, 0);

    var boardName = string.IsNullOrWhiteSpace(req.Board) ? "default" : req.Board.Trim();
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // "values" means y-against-index, which is a signal plot, not geometry — so a
    // chart created by such a push starts with equal aspect off. Geometry pushes
    // (x/y/z or points) keep it on, which is what you want for CAD point sets.
    var indexBased = req.Y is not { Length: > 0 } && req.Points is not { Length: > 0 } && req.Values is { Length: > 0 };

    var payload = store.Mutate(boardName, board =>
    {
        var isNewChart = !board.Charts.Any(c => c.Name.Equals(
            string.IsNullOrWhiteSpace(req.Chart) ? "main" : req.Chart.Trim(), StringComparison.OrdinalIgnoreCase));
        var chart = Store.GetOrAddChart(board, req.Chart);
        if (isNewChart && indexBased) chart.Uniform = false;
        if (req.ClearChart == true) chart.Series.Clear();
        if (!string.IsNullOrWhiteSpace(req.Mode))
        {
            var m = req.Mode!.Trim().ToLowerInvariant();
            if (m is "2d" or "3d" or "auto") chart.Mode = m;
        }

        var name = string.IsNullOrWhiteSpace(req.Series) ? "series" : req.Series.Trim();
        var replace = req.Replace ?? true;
        var existing = chart.Series.FirstOrDefault(s => s.Name.Equals(name, StringComparison.Ordinal));

        Series target;
        if (existing is not null && replace)
        {
            target = existing;
        }
        else
        {
            if (existing is not null) name = UniqueName(chart, name);
            target = new Series { Name = name };
            chart.Series.Add(target);
        }

        if (req.Style is { } s)
        {
            if (!string.IsNullOrWhiteSpace(s.Mode)) target.Style.Mode = s.Mode;
            if (s.Size > 0) target.Style.Size = s.Size;
            if (!string.IsNullOrWhiteSpace(s.Color)) target.Style.Color = s.Color;
            if (s.Slot.HasValue) target.Style.Slot = s.Slot;
        }
        // Assign a palette slot once, on creation, and never re-assign: colour
        // follows the entity, so removing series 2 must not repaint series 3.
        target.Style.Slot ??= Store.NextSlot(chart);

        target.X = x;
        target.Y = y;
        target.Z = z;
        target.UpdatedMs = now;
        if (req.Meta is not null) target.Meta = req.Meta;

        return new
        {
            type = "series",
            chart = chart.Name,
            mode = chart.Mode,
            uniform = chart.Uniform,
            series = new
            {
                name = target.Name,
                x,
                y,
                z,
                style = target.Style,
                visible = target.Visible,
                updatedMs = target.UpdatedMs,
                meta = target.Meta,
            },
        };
    });

    await hub.BroadcastAsync(boardName, payload);
    return (true, $"{y.Length} point(s)" + note, y.Length);
}

static string UniqueName(Chart chart, string baseName)
{
    for (var i = 2; ; i++)
    {
        var candidate = $"{baseName} #{i}";
        if (!chart.Series.Any(s => s.Name.Equals(candidate, StringComparison.Ordinal))) return candidate;
    }
}

static (double[] X, double[] Y, double[]? Z, string Note) Normalize(PushRequest r)
{
    if (r.Y is { Length: > 0 } yy)
    {
        var x = r.X is { Length: > 0 } ? r.X : Enumerable.Range(0, yy.Length).Select(i => (double)i).ToArray();
        var n = Math.Min(x.Length, yy.Length);
        var z = r.Z is { Length: > 0 } ? r.Z : null;
        if (z is not null) n = Math.Min(n, z.Length);
        var trimmed = n != yy.Length || n != x.Length || (z is not null && n != z.Length);
        return (x[..n], yy[..n], z?[..n], trimmed ? " (arrays trimmed to shortest)" : "");
    }

    if (r.Points is { Length: > 0 } pts)
    {
        var rows = pts.Where(p => p is { Length: >= 2 }).ToArray();
        if (rows.Length == 0) return ([], [], null, "");
        var dim = 3;
        foreach (var p in rows) dim = Math.Min(dim, p.Length);
        var x = new double[rows.Length];
        var y = new double[rows.Length];
        var z = dim >= 3 ? new double[rows.Length] : null;
        for (var i = 0; i < rows.Length; i++)
        {
            x[i] = rows[i][0];
            y[i] = rows[i][1];
            if (z is not null) z[i] = rows[i][2];
        }
        var dropped = pts.Length - rows.Length;
        return (x, y, z, dropped > 0 ? $" ({dropped} short row(s) dropped)" : "");
    }

    if (r.Values is { Length: > 0 } vals)
    {
        var x = Enumerable.Range(0, vals.Length).Select(i => (double)i).ToArray();
        return (x, vals, null, "");
    }

    if (!string.IsNullOrWhiteSpace(r.Text))
    {
        var p = TextPoints.Parse(r.Text!);
        return (p.X, p.Y, p.Z, p.Skipped > 0 ? $" ({p.Skipped} unparseable line(s) skipped)" : "");
    }

    return ([], [], null, "");
}

// ------------------------------------------------------------------ middleware

// Any local process may push (that's the point), but a random web page must not
// be able to drive the plots via a cross-origin form post.
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsPost(ctx.Request.Method))
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsLocalOrigin(origin, port))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("cross-origin POST rejected");
            return;
        }
    }
    await next();
});

static bool IsLocalOrigin(string origin, int port) =>
    origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase) ||
    origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
    origin.Equals($"http://[::1]:{port}", StringComparison.OrdinalIgnoreCase);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();

// ------------------------------------------------------------------- endpoints

app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    product = "PlotBridge",
    version = "0.1.0",
    port,
    dataDir,
    boards = store.BoardNames(),
}));

app.MapGet("/boards", () => Results.Json(store.BoardNames()));

app.MapGet("/snapshot", (string? board) => Results.Json(store.Snapshot(board ?? "default"), json));

app.MapPost("/push", async (HttpRequest http) =>
{
    PushRequest req;
    var contentType = http.ContentType ?? "";

    if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            req = await JsonSerializer.DeserializeAsync<PushRequest>(http.Body, json) ?? new PushRequest();
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { ok = false, error = "malformed JSON: " + ex.Message });
        }
    }
    else
    {
        using var reader = new StreamReader(http.Body, Encoding.UTF8);
        req = new PushRequest { Text = await reader.ReadToEndAsync() };
    }

    // Query string always wins, so text/plain posts can be fully addressed.
    var q = http.Query;
    if (q["board"].FirstOrDefault() is { Length: > 0 } qb) req.Board = qb;
    if (q["chart"].FirstOrDefault() is { Length: > 0 } qc) req.Chart = qc;
    if (q["series"].FirstOrDefault() is { Length: > 0 } qs) req.Series = qs;
    if (q["mode"].FirstOrDefault() is { Length: > 0 } qm) req.Mode = qm;
    if (q["replace"].FirstOrDefault() is { Length: > 0 } qr && bool.TryParse(qr, out var rb)) req.Replace = rb;
    if (q["color"].FirstOrDefault() is { Length: > 0 } qcol) (req.Style ??= new Style()).Color = qcol;

    var (ok, message, count) = await ApplyPushAsync(req);
    return ok
        ? Results.Json(new { ok, message, count })
        : Results.BadRequest(new { ok, error = message });
});

app.MapPost("/clear", async (string? board, string? chart) =>
{
    var boardName = string.IsNullOrWhiteSpace(board) ? "default" : board.Trim();
    var msg = store.Mutate(boardName, b =>
    {
        if (string.IsNullOrWhiteSpace(chart))
        {
            b.Charts.Clear();
            return (object)new { type = "clearBoard" };
        }
        var c = b.Charts.FirstOrDefault(c => c.Name.Equals(chart, StringComparison.OrdinalIgnoreCase));
        c?.Series.Clear();
        return new { type = "clearChart", chart = c?.Name ?? chart };
    });
    await hub.BroadcastAsync(boardName, msg);
    return Results.Json(new { ok = true });
});

app.Map("/ws", async (HttpContext ctx, string? board) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var boardName = string.IsNullOrWhiteSpace(board) ? "default" : board.Trim();
    var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var client = new Hub.Client { Id = Guid.NewGuid().ToString("n"), Board = boardName, Socket = socket };
    hub.Add(client);
    log.LogInformation("Page attached to board {Board} ({Count} watching)", boardName, hub.CountFor(boardName));

    try
    {
        await hub.SendAsync(client, new { type = "snapshot", clientId = client.Id, board = store.Snapshot(boardName) });

        var buffer = new byte[64 * 1024];
        var accum = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            accum.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(accum.ToArray());
            accum.SetLength(0);
            try { await HandleClientMessageAsync(client, text); }
            catch (Exception ex) { log.LogWarning("Bad client message: {Message}", ex.Message); }
        }
    }
    catch (WebSocketException) { /* page closed abruptly — normal */ }
    finally
    {
        hub.Remove(client.Id);
        log.LogInformation("Page detached from board {Board} ({Count} watching)", boardName, hub.CountFor(boardName));
    }
});

async Task HandleClientMessageAsync(Hub.Client client, string text)
{
    using var doc = JsonDocument.Parse(text);
    var root = doc.RootElement;
    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
    if (type is null) return;

    string? Str(string prop) => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    var chartName = Str("chart");
    var seriesName = Str("series");

    // The paste panel and the file-drop path share the normal push pipeline.
    if (type == "pushText")
    {
        await ApplyPushAsync(new PushRequest
        {
            Board = client.Board,
            Chart = chartName,
            Series = seriesName,
            Mode = Str("mode"),
            Text = Str("text"),
            Replace = root.TryGetProperty("replace", out var rp) && rp.ValueKind is JsonValueKind.True or JsonValueKind.False ? rp.GetBoolean() : null,
            Meta = new Dictionary<string, string> { ["source"] = "paste" },
        });
        return;
    }

    var broadcast = store.Mutate<object?>(client.Board, board =>
    {
        var chart = chartName is null ? null : board.Charts.FirstOrDefault(c => c.Name.Equals(chartName, StringComparison.Ordinal));

        switch (type)
        {
            case "addChart":
            {
                var c = Store.GetOrAddChart(board, chartName);
                return new { type = "chartAdded", chart = c.Name, mode = c.Mode, uniform = c.Uniform };
            }
            case "removeChart":
                if (chart is not null) board.Charts.Remove(chart);
                return new { type = "chartRemoved", chart = chartName };

            case "clearChart":
                chart?.Series.Clear();
                return new { type = "clearChart", chart = chartName };

            case "removeSeries":
            {
                var s = chart?.Series.FirstOrDefault(s => s.Name.Equals(seriesName, StringComparison.Ordinal));
                if (s is not null) chart!.Series.Remove(s);
                return new { type = "seriesRemoved", chart = chartName, series = seriesName };
            }
            case "setChartOpts":
            {
                if (chart is null) return null;
                if (Str("mode") is { Length: > 0 } m && m is "2d" or "3d" or "auto") chart.Mode = m;
                if (root.TryGetProperty("uniform", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False) chart.Uniform = u.GetBoolean();
                return new { type = "chartOpts", chart = chart.Name, mode = chart.Mode, uniform = chart.Uniform };
            }
            case "setVisible":
            {
                var s = chart?.Series.FirstOrDefault(s => s.Name.Equals(seriesName, StringComparison.Ordinal));
                if (s is null) return null;
                if (root.TryGetProperty("visible", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Visible = v.GetBoolean();
                return new { type = "seriesVisible", chart = chartName, series = seriesName, visible = s.Visible };
            }
            case "setStyle":
            {
                var s = chart?.Series.FirstOrDefault(s => s.Name.Equals(seriesName, StringComparison.Ordinal));
                if (s is null || !root.TryGetProperty("style", out var st) || st.ValueKind != JsonValueKind.Object) return null;
                if (st.TryGetProperty("mode", out var sm) && sm.ValueKind == JsonValueKind.String) s.Style.Mode = sm.GetString()!;
                if (st.TryGetProperty("size", out var ss) && ss.ValueKind == JsonValueKind.Number) s.Style.Size = ss.GetDouble();
                if (st.TryGetProperty("color", out var sc)) s.Style.Color = sc.ValueKind == JsonValueKind.Null ? null : sc.GetString();
                return new { type = "seriesStyle", chart = chartName, series = seriesName, style = s.Style };
            }
            case "renameSeries":
            {
                var s = chart?.Series.FirstOrDefault(s => s.Name.Equals(seriesName, StringComparison.Ordinal));
                var to = Str("to");
                if (s is null || string.IsNullOrWhiteSpace(to)) return null;
                s.Name = chart!.Series.Any(o => o != s && o.Name.Equals(to, StringComparison.Ordinal)) ? UniqueName(chart, to) : to;
                return new { type = "seriesRenamed", chart = chartName, series = seriesName, to = s.Name };
            }
            default:
                return null;
        }
    });

    // The editing page already applied the change optimistically; only the other
    // pages watching this board need telling.
    if (broadcast is not null) await hub.BroadcastAsync(client.Board, broadcast, exceptId: client.Id);
}

// ------------------------------------------------------------------- lifecycle

using var watcher = new DropWatcher(dataDir, req => ApplyPushAsync(req), log);
var saveLoop = store.RunSaveLoopAsync(app.Lifetime.ApplicationStopping);
app.Lifetime.ApplicationStopped.Register(() => store.FlushPending());

log.LogInformation("PlotBridge listening on http://localhost:{Port}", port);
log.LogInformation("Drop folder: {Folder}", watcher.Folder);

try
{
    await app.RunAsync();
}
catch (IOException ex)
{
    log.LogError("Could not bind port {Port} — is another PlotBridge already running? ({Message})", port, ex.Message);
    return 1;
}

await saveLoop;
return 0;
