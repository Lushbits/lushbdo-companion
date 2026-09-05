namespace LushbdoCompanion;

/// <summary>
/// The sender's memory: every confirmed pickup the site has not yet taken, and
/// the one parcel of them minted and waiting for, or on, the wire.
///
/// Pure on purpose — no clock, no network — because what it decides is about
/// *time*, and that is what the tests have to be able to say. A pickup is an
/// event at an instant; a gather session is live between Start and Stop, less
/// every break the member takes with Pause; and the loot that counts is the
/// loot picked up while the session was live. Nothing else does — not what
/// came before Start, and not what an alt looted from a boss while the run
/// stood paused (owner, 2026-09-05). The sender used to hold what it read
/// while no session ran and deliver all of it the moment one started, so a
/// session opened after an afternoon's grinding began with the afternoon on
/// it. The site leaves the first half of this to the app on purpose — a
/// `no-session` batch is answered and not claimed, so an app that re-posts it
/// has it land — and does the second half itself, claiming a `paused` batch
/// and filing nothing. What is left is the loot the app has *not yet posted*
/// when the session goes live, and that is this class's job.
///
/// The app cannot see the session: an answer is the only word it gets. So it
/// learns the two facts an answer can carry, and each is a cut on the pool:
///
///   - **Not live** — `no-session` or `paused` — at the instant a post was
///     sent means the session was not live then, so any live stretch that
///     follows begins after that instant. Everything in the parcel, and every
///     pending pickup seen before the send, is outside it and is dropped. A
///     pickup seen after the send is kept: the round trip is the one window
///     the answer cannot speak for.
///   - **Live** — the post landed, or a loot-less post was answered with a
///     session — says when the session went live, which places that instant
///     on this clock. Pending pickups seen before it are dropped, and the
///     parcel that just landed is checked the same way — too late to hold it
///     back, not too late to say so.
///
/// The parcel that lands is the one thing that can still carry loot from
/// outside the session: it had to be sent to find out the session was live,
/// and everything in it was picked up before the answer came. The sender
/// avoids that by asking with an empty post while the last answer was not
/// live. A site that refuses the question gets the next best thing: a
/// **probe** — one pickup, the oldest — while the rest waits in the pool,
/// where the live instant, once known, cuts it exactly. That is what
/// <see cref="Probing"/> means.
///
/// The live instant is placed to about a second: the answer's figure is in
/// whole seconds, it took a leg to arrive, and a pickup's instant is when the
/// board confirmed it, which trails the line's appearance by a tick or so.
/// Those lean opposite ways and roughly cancel; where they do not, a pickup
/// on the boundary is dropped rather than kept, the direction every ambiguity
/// in this app resolves. The one thing the cut can wrongly drop is a live
/// pickup delivered late across a break — a site outage lasting through a
/// pause, with the pickup pooled from before it — and that is a visible
/// undercount, the same direction.
/// </summary>
public sealed class LootPool
{
    /// <summary>
    /// Quiet time after the last confirmed pickup before the pool ships. A
    /// gulp of loot lands as several pickups a few hundred ms apart and should
    /// still travel as one batch, but a lone drop should not wait on a timer
    /// for a burst that is not coming — the member is watching the site for it.
    /// </summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The ceiling on that wait. Loot that never stops arriving would otherwise
    /// never find a quiet moment, and the pool would grow instead of shipping.
    /// </summary>
    public static readonly TimeSpan MintPace = TimeSpan.FromSeconds(3);

    /// <summary>One confirmed pickup and the instant the board confirmed it.</summary>
    public readonly record struct Pickup(string Name, int Count, DateTime SeenAt);

    /// <summary>
    /// A batch as minted: one id for every delivery of it, so the site's ring
    /// recognises a redelivery; the wire lines, same names merged; and the
    /// pickups behind them, kept so a landing can still say when each was seen.
    /// </summary>
    public sealed record Parcel(string Id, IReadOnlyList<(string Name, int Count)> Lines, IReadOnlyList<Pickup> Pickups);

    private readonly List<Pickup> _pending = [];
    private Parcel? _parcel;
    private DateTime _lastAdd = DateTime.MinValue;
    private DateTime _pooledSince = DateTime.MinValue;
    private bool _probing;

    /// <summary>Pickups the site has not taken: the parcel's and the pool's.</summary>
    public int Count => (_parcel?.Pickups.Count ?? 0) + _pending.Count;

    /// <summary>
    /// True while the last answer said the session is not live — none, or
    /// paused. The sender asks before it sends in this state, and a site that
    /// will not be asked gets a one-pickup probe from <see cref="Mint"/>.
    /// </summary>
    public bool Probing => _probing;

    /// <summary>A confirmed pickup from the board, at the instant it was confirmed.</summary>
    public void Add(string name, int count, DateTime now)
    {
        _lastAdd = now;
        if (_pending.Count == 0) _pooledSince = now;
        _pending.Add(new Pickup(name, count, now));
    }

    /// <summary>
    /// The parcel to send: the one already minted, or a new one once the
    /// drops have stopped or have gone on long enough that waiting for quiet
    /// is waiting forever. Null when there is nothing to send. Allocates only
    /// when it mints, so an empty pool costs nothing per ask.
    /// </summary>
    public Parcel? Mint(DateTime now)
    {
        if (_parcel is not null) return _parcel;
        if (_pending.Count == 0) return null;

        var settled = now - _lastAdd >= CoalesceWindow;
        var waitedLongEnough = now - _pooledSince >= MintPace;
        if (!settled && !waitedLongEnough) return null;

        List<Pickup> taken;
        if (_probing)
        {
            // One pickup, the oldest: this parcel exists to find out whether
            // the session is live, and everything it carries may be from
            // outside it.
            taken = [_pending[0]];
            _pending.RemoveAt(0);
        }
        else
        {
            taken = [.. _pending];
            _pending.Clear();
        }

        _parcel = new Parcel($"companion-{Guid.NewGuid():N}", Merge(taken), taken);
        return _parcel;
    }

    /// <summary>
    /// The session was not live — there was none, or it was paused — when the
    /// post went out at <paramref name="sentAt"/>. The parcel and every
    /// pending pickup seen before then are outside any live stretch and are
    /// dropped; returns how many.
    /// </summary>
    public int NotLive(DateTime sentAt)
    {
        var dropped = _parcel?.Pickups.Count ?? 0;
        _parcel = null;
        dropped += _pending.RemoveAll(p => p.SeenAt < sentAt);
        _probing = true;
        return dropped;
    }

    /// <summary>
    /// The session is live, and has been since <paramref name="liveSince"/>
    /// (null when the answer did not say). Pending pickups seen before that
    /// instant are dropped. Returns how many of the parcel's own pickups were
    /// seen before it — landed, and not to be held back now, but said — and
    /// how many pending ones were cut.
    /// </summary>
    public (int Rode, int Pruned) Live(DateTime? liveSince)
    {
        var rode = 0;
        var pruned = 0;
        if (liveSince is { } at)
        {
            if (_parcel is not null)
                foreach (var p in _parcel.Pickups)
                    if (p.SeenAt < at) rode++;
            pruned = _pending.RemoveAll(p => p.SeenAt < at);
        }
        _parcel = null;
        _probing = false;
        return (rode, pruned);
    }

    /// <summary>The site answered the parcel with something this app does not hold on to.</summary>
    public void Discard() => _parcel = null;

    private static List<(string Name, int Count)> Merge(List<Pickup> pickups)
    {
        var lines = new List<(string Name, int Count)>(pickups.Count);
        foreach (var p in pickups)
        {
            var merged = false;
            for (var i = 0; i < lines.Count && !merged; i++)
            {
                if (!string.Equals(lines[i].Name, p.Name, StringComparison.Ordinal)) continue;
                lines[i] = (p.Name, lines[i].Count + p.Count);
                merged = true;
            }
            if (!merged) lines.Add((p.Name, p.Count));
        }
        return lines;
    }
}
