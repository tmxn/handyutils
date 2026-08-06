using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PlotBridge.Server;

/// <summary>Fan-out of board updates to every page watching that board.</summary>
public sealed class Hub
{
    public sealed class Client
    {
        public required string Id { get; init; }
        public required string Board { get; init; }
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly ILogger _log;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Hub(ILogger log) => _log = log;

    public int CountFor(string board) =>
        _clients.Values.Count(c => c.Board.Equals(board, StringComparison.OrdinalIgnoreCase));

    public void Add(Client c) => _clients[c.Id] = c;
    public void Remove(string id) => _clients.TryRemove(id, out _);

    /// <summary>Send to every client on the board except <paramref name="exceptId"/>
    /// (used so a page that just edited a style doesn't fight its own echo).</summary>
    public async Task BroadcastAsync(string board, object message, string? exceptId = null)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));
        var targets = _clients.Values
            .Where(c => c.Board.Equals(board, StringComparison.OrdinalIgnoreCase) && c.Id != exceptId)
            .ToArray();

        foreach (var c in targets) await SendRawAsync(c, payload);
    }

    public Task SendAsync(Client c, object message) =>
        SendRawAsync(c, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json)));

    private async Task SendRawAsync(Client c, byte[] payload)
    {
        if (c.Socket.State != WebSocketState.Open) return;
        await c.SendGate.WaitAsync();
        try
        {
            await c.Socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Dropping client {Id}: {Message}", c.Id, ex.Message);
            Remove(c.Id);
        }
        finally
        {
            c.SendGate.Release();
        }
    }
}
