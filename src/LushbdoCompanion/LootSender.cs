namespace LushbdoCompanion;

/// <summary>
/// The pipe from the board to the site. Confirmed pickups pool here and leave
/// as a batch every few seconds: one batch in flight at a time, its id minted
/// client-side at packaging so the server's idempotency ring makes redelivery
/// safe. `applied:false, reason:"no-session"` is not an error — the batch is
/// held and re-posted quietly until a gather session runs. An unreachable
/// site backs off without nagging; the log speaks only when the state
/// changes. A 401 means the member revoked this device: say so once, raise
/// <see cref="Revoked"/>, and stop for good.
/// </summary>
public sealed class LootSender : IDisposable
{
    private static readonly TimeSpan MintPace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NoSessionRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackoffFloor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffCeiling = TimeSpan.FromSeconds(60);

    private enum Flow { Flowing, NoSession, Unreachable }

    private readonly IngestClient _client;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<IngestClient.Line> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private IngestClient.Batch? _inFlight;
    private DateTime _nextAttempt = DateTime.MinValue;
    private TimeSpan _backoff = BackoffFloor;
    private Flow _flow = Flow.Flowing;
    private DateTime _lastHoldNote = DateTime.MinValue;
    private long _sentLines;

    /// <summary>The site said 401 — the token is revoked. Raised once, from a worker thread.</summary>
    public event Action<string>? Revoked;

    public long SentLines => Interlocked.Read(ref _sentLines);

    public LootSender(IngestClient client, Action<string> log)
    {
        _client = client;
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>A confirmed pickup from the board. Counts are increments; same names merge within a batch.</summary>
    public void Add(string name, int count)
    {
        lock (_gate)
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                if (!string.Equals(_pending[i].Name, name, StringComparison.Ordinal)) continue;
                _pending[i] = _pending[i] with { Count = _pending[i].Count + count };
                return;
            }
            _pending.Add(new IngestClient.Line(name, count));
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(MintPace);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                IngestClient.Batch? batch;
                lock (_gate)
                {
                    if (_inFlight is null && _pending.Count > 0)
                    {
                        _inFlight = new IngestClient.Batch($"companion-{Guid.NewGuid():N}", [.. _pending]);
                        _pending.Clear();
                    }
                    batch = _inFlight;
                }
                if (batch is null || DateTime.UtcNow < _nextAttempt) continue;

                if (!await DeliverAsync(batch)) return; // revoked — the loop is done for good
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed — nothing to say.
        }
    }

    /// <summary>False means stop everything: the token was rejected.</summary>
    private async Task<bool> DeliverAsync(IngestClient.Batch batch)
    {
        var result = await _client.SendAsync(batch, _stop.Token);

        if (result.Status is 401 or 403)
        {
            var why = result.Error ?? "the site rejected the token.";
            _log($"sent  nothing — {why}");
            Revoked?.Invoke(why);
            return false;
        }

        if (!result.Ok || result.Answer is null)
        {
            if (_flow != Flow.Unreachable)
            {
                _flow = Flow.Unreachable;
                _log($"hold  {result.Error} Retrying quietly; nothing is lost.");
            }
            _nextAttempt = DateTime.UtcNow + _backoff;
            _backoff = _backoff + _backoff <= BackoffCeiling ? _backoff + _backoff : BackoffCeiling;
            return true;
        }

        _backoff = BackoffFloor;
        var answer = result.Answer;

        if (!answer.Applied)
        {
            if (answer.Reason == "no-session")
            {
                var held = HeldLineCount();
                if (_flow != Flow.NoSession || DateTime.UtcNow - _lastHoldNote > TimeSpan.FromMinutes(1))
                {
                    _flow = Flow.NoSession;
                    _lastHoldNote = DateTime.UtcNow;
                    _log($"hold  {held} line(s) — no gather session is running; press Start on the site's /gather page.");
                }
                _nextAttempt = DateTime.UtcNow + NoSessionRetry;
                return true;
            }

            // An answer this version does not know how to hold on to. Dropping
            // the batch is the only move that cannot repeat forever — say so.
            _log($"drop  batch of {batch.Lines.Count} line(s) — the site did not apply it ({answer.Reason ?? "no reason given"}).");
            lock (_gate) _inFlight = null;
            return true;
        }

        if (_flow != Flow.Flowing)
        {
            _log(_flow == Flow.NoSession
                ? "sent  a gather session is running again — the held loot has landed."
                : "sent  the site is reachable again — the held loot has landed.");
            _flow = Flow.Flowing;
        }

        foreach (var m in answer.Matched ?? [])
            _log($"sent  {m.Name}  +{m.Added} → {m.Qty}");
        foreach (var h in answer.Held ?? [])
            _log($"held  \"{h.LineText}\" ×{h.Count}  ({h.Why}) — resolve it on the session page.");
        foreach (var d in answer.Dropped ?? [])
            _log($"drop  \"{d.LineText}\" ×{d.Count}  ({d.Why})");

        Interlocked.Add(ref _sentLines, batch.Lines.Count);
        lock (_gate)
        {
            _inFlight = null;
            _nextAttempt = DateTime.MinValue;
        }
        return true;
    }

    private int HeldLineCount()
    {
        lock (_gate)
            return (_inFlight?.Lines.Count ?? 0) + _pending.Count;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* cancellation surfacing through Wait */ }

        var abandoned = HeldLineCount();
        if (abandoned > 0)
            _log($"drop  {abandoned} unsent line(s) discarded — watching stopped before they could be delivered.");
        _stop.Dispose();
    }
}
