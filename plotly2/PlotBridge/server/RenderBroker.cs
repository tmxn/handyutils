namespace PlotBridge.Server;

/// <summary>
/// Turns a board into a PNG by asking a connected page to do it. Plotly's
/// rasteriser lives in the browser, so the alternative would be a headless
/// Chromium dependency for the whole server; instead the page that is already
/// attached does the work and posts the bytes back.
/// </summary>
/// <remarks>
/// The consequence is worth stating plainly: <b>a render needs a page open on that
/// board.</b> With no page attached the request fails fast rather than hanging, so
/// a script gets a clear 503 instead of a timeout it has to interpret.
///
/// The page renders into its own off-screen div rather than the visible one, so a
/// render never moves the tab, camera or zoom of whoever is watching. (Off-screen
/// rather than detached: Plotly sizes axes from the laid-out box, and an element
/// outside the document has none.)
/// </remarks>
public sealed class RenderBroker
{
    public sealed record Result(byte[] Bytes, string ContentType, string? Mode);

    private readonly Dictionary<string, TaskCompletionSource<Result>> _pending = new();
    private readonly object _gate = new();
    private readonly ILogger _log;

    public RenderBroker(ILogger log) => _log = log;

    /// <summary>Registers a pending render and returns its id plus the task that
    /// completes when the page posts bytes back.</summary>
    public (string Id, Task<Result> Completion) Begin()
    {
        // RunContinuationsAsynchronously: the completion runs on the /render/result
        // request thread, and we must not continue the waiting /render handler on it.
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid().ToString("n");
        lock (_gate) _pending[id] = tcs;
        return (id, tcs.Task);
    }

    /// <summary>Completes a pending render. Returns false when the id is unknown,
    /// which normally means it already timed out and was abandoned.</summary>
    public bool Complete(string id, byte[] bytes, string contentType, string? mode)
    {
        TaskCompletionSource<Result>? tcs;
        lock (_gate)
        {
            if (!_pending.Remove(id, out tcs)) return false;
        }
        return tcs.TrySetResult(new Result(bytes, contentType, mode));
    }

    /// <summary>Reports a page-side failure so the waiter gets the real reason
    /// instead of a timeout.</summary>
    public bool Fail(string id, string reason)
    {
        TaskCompletionSource<Result>? tcs;
        lock (_gate)
        {
            if (!_pending.Remove(id, out tcs)) return false;
        }
        _log.LogWarning("Render {Id} failed on the page: {Reason}", id, reason);
        return tcs.TrySetException(new InvalidOperationException(reason));
    }

    /// <summary>Drops a pending render that nobody is waiting for any more.</summary>
    public void Abandon(string id)
    {
        lock (_gate) _pending.Remove(id);
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }
}
