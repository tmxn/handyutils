namespace PlotBridge.Server;

/// <summary>
/// A short, in-memory history of the images produced by <c>/render</c>, so a human
/// can watch what an automated caller is looking at.
/// </summary>
/// <remarks>
/// <para>
/// <c>/render</c> hands its PNG straight back to whoever asked and keeps nothing.
/// That is fine for a script and useless for the person sitting next to it: whether
/// the picture ever gets seen depends on the caller volunteering a file path and
/// not cleaning it up. This keeps the last few renders addressable so the feed page
/// can show them, and deliberately keeps them <b>in memory only</b> - writing them
/// to disk would recreate the litter this exists to avoid.
/// </para>
/// <para>
/// Failed attempts are recorded too, with a reason and no bytes. An agent that
/// renders against a board with no page open otherwise leaves no trace at all, and
/// an empty feed reads as "nothing happened" rather than "it tried and could not".
/// </para>
/// </remarks>
public sealed class RenderFeed
{
    /// <summary>One render attempt. <paramref name="Bytes"/> is null when it failed.</summary>
    public sealed record Shot(
        string Id,
        long AtMs,
        string Board,
        string Chart,
        string? Eye,
        string? Up,
        int Width,
        int Height,
        double Scale,
        string? Mode,
        string ContentType,
        byte[]? Bytes,
        string? Error);

    private readonly LinkedList<Shot> _shots = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly long _byteCap;
    private long _version;
    private long _bytesHeld;

    // Replaced on every change; waiters hold the old one and are released together.
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RenderFeed(int capacity = 10, long byteCap = 96L * 1024 * 1024)
    {
        _capacity = Math.Max(1, capacity);
        _byteCap = Math.Max(1024 * 1024, byteCap);
    }

    public int Capacity => _capacity;

    public long Version { get { lock (_gate) return _version; } }

    public void Add(Shot shot)
    {
        lock (_gate)
        {
            _shots.AddFirst(shot);
            _bytesHeld += shot.Bytes?.Length ?? 0;

            // Count first, then size: a single enormous render must not be able to
            // push out everything behind it, but ten of them still have to fit.
            while (_shots.Count > _capacity) DropOldest();
            while (_bytesHeld > _byteCap && _shots.Count > 1) DropOldest();

            _version++;
            var previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private void DropOldest()
    {
        var last = _shots.Last;
        if (last is null) return;
        _bytesHeld -= last.Value.Bytes?.Length ?? 0;
        _shots.RemoveLast();
    }

    /// <summary>Newest first, together with the version they were read at.</summary>
    public (long Version, IReadOnlyList<Shot> Shots) List()
    {
        lock (_gate) return (_version, _shots.ToArray());
    }

    public Shot? Get(string id)
    {
        lock (_gate) return _shots.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// Completes as soon as the feed differs from <paramref name="since"/>, or when
    /// the budget runs out. Lets the page update the instant a render lands without
    /// polling for it - and a timed-out wait is a normal answer, not an error.
    /// </summary>
    public async Task WaitForChangeAsync(long since, TimeSpan budget, CancellationToken cancel)
    {
        Task wait;
        lock (_gate)
        {
            if (_version != since) return;
            wait = _changed.Task;
        }

        await Task.WhenAny(wait, Task.Delay(budget, cancel));
    }
}
