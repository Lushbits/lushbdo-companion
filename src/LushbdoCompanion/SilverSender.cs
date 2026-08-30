namespace LushbdoCompanion;

/// <summary>
/// The pipe from <see cref="BalanceBoard"/> to the site — and deliberately not
/// a second <see cref="LootSender"/>, because a balance is a **level** and loot
/// lines are **increments**. Every part of the loot sender's shape follows from
/// that difference and none of it transfers (#24):
///
/// - **Only the newest value matters.** An older reading is worthless the
///   moment a newer one is confirmed, so this holds *one* figure and replaces
///   it. Nothing queues, nothing merges, nothing is lost by being superseded —
///   which is also why <see cref="Dispose"/> has no "unsent lines discarded"
///   note to write. An undelivered balance is not lost data; it is a stale
///   figure a later reading will replace.
/// - **It is not session-scoped.** A balance is account state, true with no
///   gather run open, so there is no `no-session` hold and nothing to wait for.
/// - **Change-only.** A figure equal to the one the site already took is not
///   news. The route agrees — it writes nothing and lands no entry for a
///   repeat, which is what makes it idempotent with no batch id.
///
/// What *is* worth reusing is everything about talking to the site rather than
/// about what is being said: the 401 stop, the quiet backoff, and the `sent` /
/// `hold` / `drop` register the log already speaks.
///
/// ## The cadence
///
/// #24 asks this issue to state a real number rather than inherit one, because
/// bdo#665 was sizing its ring against "5 s was the figure discussed" — ten
/// posts a minute from a tray app, which is not what this should do.
///
/// The natural rate is already very low: the figure is only readable while the
/// market panel is open, a member opens it a handful of times in a session, and
/// only a *change* is ever posted. So <see cref="Floor"/> is not a throttle on
/// normal use — normal use never comes near it. It is a bound on a flapping
/// misread, which is the one way this could become chatty, and it caps that at
/// 30 posts an hour however badly a rectangle is behaving. Two minutes matches
/// the watcher's own heartbeat and <see cref="BalanceBoard.RepeatNoteTicks"/>,
/// so the app has one idea of "how often is often enough" rather than three.
///
/// A change held by the floor is not dropped — it goes when the floor lifts,
/// and if a newer figure arrives first that newer one goes instead.
/// </summary>
public sealed class SilverSender : IDisposable
{
    /// <summary>How often the loop looks. Nothing here is urgent; it usually finds nothing.</summary>
    private static readonly TimeSpan PollPace = TimeSpan.FromSeconds(1);

    /// <summary>The least time between two posts. See the cadence note above.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan BackoffFloor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffCeiling = TimeSpan.FromMinutes(2);

    private readonly IngestClient _client;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private long? _pending;        // the newest confirmed figure the site has not taken
    private long? _accepted;       // what the site last took, or folded as unchanged
    private long? _refused;        // a figure the site named a rule against; do not ask twice
    private DateTime _nextAttempt = DateTime.MinValue;
    private TimeSpan _backoff = BackoffFloor;
    private bool _unreachable;
    private long _sent;

    /// <summary>The site said 401 — the token is revoked. Raised once, from a worker thread.</summary>
    public event Action<string>? Revoked;

    public long Sent => Interlocked.Read(ref _sent);

    public SilverSender(IngestClient client, Action<string> log)
    {
        _client = client;
        _log = log;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// A confirmed figure from the board. Replaces whatever was waiting: the
    /// newest reading is the only one worth posting.
    /// </summary>
    public void Record(long silver)
    {
        lock (_gate) _pending = silver;
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(PollPace);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                long figure;
                lock (_gate)
                {
                    if (_pending is not { } value) continue;
                    // Change-only, and never twice about a figure the site has
                    // already named a rule against.
                    if (value == _accepted || value == _refused)
                    {
                        _pending = null;
                        continue;
                    }
                    if (DateTime.UtcNow < _nextAttempt) continue;
                    figure = value;
                }

                if (!await DeliverAsync(figure)) return; // revoked — done for good
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed — nothing to say.
        }
    }

    /// <summary>False means stop everything: the token was rejected.</summary>
    private async Task<bool> DeliverAsync(long silver)
    {
        var result = await _client.RecordSilverAsync(silver, _stop.Token);

        if (result.Status is 401 or 403)
        {
            var why = result.Error ?? "the site rejected the token.";
            _log($"sent  no silver — {why}");
            Revoked?.Invoke(why);
            return false;
        }

        if (result.Status == 400)
        {
            // The site named a rule this figure broke. Retrying cannot fix it,
            // and the reading behind it is not coming back different, so the
            // only move that does not repeat forever is to stop asking about
            // this one figure — a later, different reading is unaffected.
            lock (_gate)
            {
                _refused = silver;
                _pending = null;
            }
            _log($"drop  silver {BalanceParser.Money(silver)} — the site would not take it: {result.Error}");
            return true;
        }

        if (!result.Ok || result.Answer is null)
        {
            if (!_unreachable)
            {
                _unreachable = true;
                _log($"hold  {result.Error} Retrying quietly; a newer reading would replace this one.");
            }
            lock (_gate)
            {
                // 503 says how long; anything else backs off on its own curve.
                _nextAttempt = DateTime.UtcNow + (result.RetryAfter ?? _backoff);
            }
            _backoff = _backoff + _backoff <= BackoffCeiling ? _backoff + _backoff : BackoffCeiling;
            return true;
        }

        _backoff = BackoffFloor;
        var answer = result.Answer;

        lock (_gate)
        {
            _accepted = silver;
            if (_pending == silver) _pending = null;
            _nextAttempt = DateTime.UtcNow + Floor;
        }
        Interlocked.Increment(ref _sent);

        if (_unreachable)
        {
            _unreachable = false;
            _log("sent  the site is reachable again.");
        }

        // `stored:false, reason:"unchanged"` is a success the route documents:
        // the figure already stood, so it deliberately wrote nothing and landed
        // no entry. Saying so is worth a line — it is the difference between
        // "your silver is current" and "nothing is happening".
        _log(answer.Stored
            ? $"sent  silver {BalanceParser.Money(answer.Silver)} recorded on the site."
            : $"sent  silver {BalanceParser.Money(answer.Silver)} — the site already had it ({answer.Reason ?? "unchanged"}).");
        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* cancellation surfacing through Wait */ }

        // Deliberately silent about anything undelivered. A pending balance is
        // not lost data the way an unsent loot line is — it is a figure that
        // was true a moment ago and will be read again the next time the panel
        // is open.
        _stop.Dispose();
    }
}
