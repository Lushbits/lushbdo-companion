namespace LushbdoCompanion;

/// <summary>
/// The pipe from the board to the site. Confirmed pickups pool in a
/// <see cref="LootPool"/> and leave as a batch as soon as the drops stop
/// arriving: one batch in flight at a time, its id minted client-side at
/// packaging so the server's idempotency ring makes redelivery safe. An
/// unreachable site backs off without nagging and keeps the batch; the log
/// speaks only when the state changes. A 401 means the member revoked this
/// device: say so once, raise <see cref="Revoked"/>, and stop for good.
///
/// `applied:false` with `reason:"no-session"` or `"paused"` is not an error
/// and it is not a reason to wait, either: loot picked up while the session is
/// not live is loot outside it, and the pool drops it rather than delivering
/// it to whatever live stretch comes next. The pool's summary says how it
/// tells the two apart. What this class adds is the pace — while the session
/// is not live it posts again fifteen seconds after the last answer, never
/// sooner, so a member grinding without a session costs the site four small
/// posts a minute — and the way it asks.
///
/// Asking is a post with no lines. The site's answer to it says whether the
/// session is live and, when it is, since when, and the pool cuts what it
/// holds at that instant *before* anything is sent — so the batch that finds
/// a live session never carries loot from before it. A site that refuses the
/// empty post (HTTP 400: every site before the ask was added) is asked no
/// further; from then on the pool's one-pickup probe finds the session and
/// the log says when that one pickup was from outside it.
/// </summary>
public sealed class LootSender : IDisposable
{
    /// <summary>How often the loop looks at the pool. Cheap: it usually finds nothing.</summary>
    private static readonly TimeSpan PollPace = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan NotLivePace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NotLiveNotePace = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BackoffFloor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffCeiling = TimeSpan.FromSeconds(60);

    private enum Flow { Flowing, NotLive, Unreachable }

    private readonly IngestClient _client;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly LootPool _pool = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private LootPool.Parcel? _minted;   // the parcel _batch was built from
    private IngestClient.Batch? _batch;
    private bool _askRefused;           // the site answered an empty post with 400: probe instead
    private DateTime _nextAttempt = DateTime.MinValue;
    private TimeSpan _backoff = BackoffFloor;
    private Flow _flow = Flow.Flowing;
    private DateTime _lastNotLiveNote = DateTime.MinValue;
    private int _droppedWhileNotLive;
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
        lock (_gate) _pool.Add(name, count, DateTime.UtcNow);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(PollPace);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                IngestClient.Batch? batch;
                var ask = false;
                lock (_gate)
                {
                    if (_pool.Probing && !_askRefused)
                    {
                        // Not live, as far as the last answer knew. Ask before
                        // sending anything: a post made to find out lands
                        // whatever it carries. Nothing pooled, nothing to ask.
                        ask = _pool.Count > 0;
                        batch = null;
                        _minted = null;
                        _batch = null;
                    }
                    else
                    {
                        var parcel = _pool.Mint(DateTime.UtcNow);
                        if (parcel is null)
                        {
                            _minted = null;
                            _batch = null;
                        }
                        else if (!ReferenceEquals(parcel, _minted))
                        {
                            _minted = parcel;
                            _batch = ToBatch(parcel);
                        }
                        batch = _batch;
                    }
                }
                if (!ask && batch is null) continue;
                if (DateTime.UtcNow < _nextAttempt) continue;

                batch ??= new IngestClient.Batch($"companion-ask-{Guid.NewGuid():N}", []);
                if (!await DeliverAsync(batch, ask)) return; // revoked — the loop is done for good
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed — nothing to say.
        }
    }

    private static IngestClient.Batch ToBatch(LootPool.Parcel parcel)
    {
        var lines = new List<IngestClient.Line>(parcel.Lines.Count);
        foreach (var (name, count) in parcel.Lines) lines.Add(new IngestClient.Line(name, count));
        return new IngestClient.Batch(parcel.Id, lines);
    }

    /// <summary>
    /// False means stop everything: the token was rejected. <paramref name="ask"/>
    /// is a post with no lines, sent to learn whether the session is live.
    /// </summary>
    private async Task<bool> DeliverAsync(IngestClient.Batch batch, bool ask)
    {
        var sentAt = DateTime.UtcNow;
        var result = await _client.SendAsync(batch, _stop.Token);
        var receivedAt = DateTime.UtcNow;

        if (result.Status is 401 or 403)
        {
            var why = result.Error ?? "the site rejected the token.";
            _log($"sent  nothing — {why}");
            Revoked?.Invoke(why);
            return false;
        }

        if (ask && result.Status == 400)
        {
            // A site from before the empty post meant anything. The probe
            // takes over from here; the pace is already spent, so it goes now.
            _askRefused = true;
            _log("This site does not answer an empty post yet, so the first pickup after a break or before Start is what finds the session — and it lands with it. The log says when that happens.");
            return true;
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

        if (!answer.Applied && answer.Reason is "no-session" or "paused")
        {
            int dropped;
            lock (_gate) dropped = _pool.NotLive(sentAt);
            _droppedWhileNotLive += dropped;

            var now = DateTime.UtcNow;
            if (_flow != Flow.NotLive || now - _lastNotLiveNote >= NotLiveNotePace)
            {
                _flow = Flow.NotLive;
                _lastNotLiveNote = now;
                _log(answer.Reason == "paused"
                    ? $"drop  {_droppedWhileNotLive} line(s) — the gather session is paused, so loot picked up now does not count. Resume it on the site's /gather page."
                    : $"drop  {_droppedWhileNotLive} line(s) — no gather session is running, so loot picked up now does not count. Press Start on the site's /gather page.");
            }
            _nextAttempt = now + NotLivePace;
            return true;
        }

        if (!answer.Applied && !ask && answer.Reason != "already-applied")
        {
            // An answer this version does not know how to hold on to. Dropping
            // the batch is the only move that cannot repeat forever — say so.
            _log($"drop  batch of {batch.Lines.Count} line(s) — the site did not apply it ({answer.Reason ?? "no reason given"}).");
            lock (_gate) _pool.Discard();
            return true;
        }

        // Live. The batch landed — on this delivery, or on an earlier one
        // whose answer never arrived and which the site's ring has just
        // recognised — or the empty post was answered with a session. Either
        // way the answer says since when, and that instant on this clock is
        // where the pool cuts. `elapsedSec` is the fallback for a site that
        // does not say: it is gathering time since Start, so after a break it
        // places an instant at or before the resume, never after it.
        DateTime? liveSince = answer.Session is { } s
            ? receivedAt - TimeSpan.FromSeconds(s.LiveSinceSec ?? s.ElapsedSec)
            : null;
        int rode, pruned;
        lock (_gate) (rode, pruned) = _pool.Live(liveSince);

        if (_flow != Flow.Flowing)
        {
            _log(_flow == Flow.NotLive
                ? "sent  the gather session is live — loot counts from here."
                : "sent  the site is reachable again — the held loot has landed.");
            _flow = Flow.Flowing;
            _droppedWhileNotLive = 0;
        }
        if (!answer.Applied && !ask)
            _log("sent  the site already had this batch — an earlier answer was lost on the way.");
        if (rode > 0)
            _log($"sent  {rode} line(s) in this batch were picked up before the session went live and landed with it — the batch that finds a live session cannot be held back. Correct it on the session page if it matters.");
        if (pruned > 0)
            _log($"drop  {pruned} line(s) picked up before the session went live — they do not count.");

        foreach (var m in answer.Matched ?? [])
            _log($"sent  {m.Name}  +{m.Added} → {m.Qty}");
        foreach (var h in answer.Held ?? [])
            _log($"held  \"{h.LineText}\" ×{h.Count}  ({h.Why}) — resolve it on the session page.");
        foreach (var d in answer.Dropped ?? [])
            _log($"drop  \"{d.LineText}\" ×{d.Count}  ({d.Why})");

        if (!ask) Interlocked.Add(ref _sentLines, batch.Lines.Count);
        _nextAttempt = DateTime.MinValue;
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* cancellation surfacing through Wait */ }

        int abandoned;
        lock (_gate) abandoned = _pool.Count;
        if (abandoned > 0)
            _log($"drop  {abandoned} unsent line(s) discarded — watching stopped before they could be delivered.");
        _stop.Dispose();
    }
}
