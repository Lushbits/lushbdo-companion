namespace LushbdoCompanion;

/// <summary>
/// The sender's memory: every confirmed pickup the site has not yet taken, and
/// the one parcel of them minted and waiting for, or on, the wire.
///
/// Pure on purpose — no clock, no network — because what it decides is about
/// *time*, and that is what the tests have to be able to say. A pickup is an
/// event at an instant; a gather session is a span the member opens with
/// Start on the site; and the loot that counts is the loot picked up inside
/// the span. Nothing else does. The sender used to hold what it read while no
/// session ran and deliver all of it the moment one started, so a session
/// opened after an afternoon's grinding began with the afternoon on it (owner,
/// 2026-09-05). The site leaves this to the app on purpose — a `no-session`
/// batch is answered and not claimed, so an app that re-posts it has it land —
/// which is why the rule has to live on this side.
///
/// The app cannot ask the site whether a session runs: an empty batch is
/// refused, and a `no-session` answer carries no session. So it learns the
/// two facts it can, each from an answer it already gets, and each is a cut
/// on the pool:
///
///   - **`no-session`** at the instant a batch was sent means no session was
///     open then, so any session that opens later opens after that instant.
///     Everything in the parcel, and every pending pickup seen before the
///     send, is loot from outside any session and is dropped. A pickup seen
///     after the send is kept: the round trip is the one window the answer
///     cannot speak for.
///   - **A landed batch** says how long the session has run (`elapsedSec`),
///     which places Start on this clock. Pending pickups seen before it are
///     dropped, and the parcel that just landed is checked the same way —
///     too late to hold it back, not too late to say so.
///
/// The parcel that lands is the one thing that can still carry pre-Start
/// loot: it had to be sent to find out that a session exists at all, and
/// everything in it was picked up before the answer came. So while the last
/// answer was `no-session` a parcel is a **probe** — one pickup, the oldest —
/// and the rest waits in the pool, where Start, once known, cuts it exactly.
///
/// Start is placed to about a second: `elapsedSec` is truncated, the answer
/// took a leg to arrive, and a pickup's instant is when the board confirmed
/// it, which trails the line's appearance by a tick or so. Those lean
/// opposite ways and roughly cancel; where they do not, a pickup on the
/// boundary is dropped rather than kept, the direction every ambiguity in
/// this app resolves.
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

    /// <summary>True while the last answer was `no-session`, so the next parcel is a probe.</summary>
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
            // One pickup, the oldest: this parcel exists to find out whether a
            // session runs, and everything it carries may predate Start.
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
    /// The site had no session open when the parcel went out at
    /// <paramref name="sentAt"/>. The parcel and every pending pickup seen
    /// before then are outside any session and are dropped; returns how many.
    /// </summary>
    public int NoSession(DateTime sentAt)
    {
        var dropped = _parcel?.Pickups.Count ?? 0;
        _parcel = null;
        dropped += _pending.RemoveAll(p => p.SeenAt < sentAt);
        _probing = true;
        return dropped;
    }

    /// <summary>
    /// The parcel landed on a session that started at <paramref name="start"/>
    /// (null when the answer did not say). Pending pickups seen before Start
    /// are dropped. Returns how many of the parcel's own pickups were seen
    /// before Start — landed, and not to be held back now, but said — and how
    /// many pending ones were cut.
    /// </summary>
    public (int Rode, int Pruned) Landed(DateTime? start)
    {
        var rode = 0;
        var pruned = 0;
        if (start is { } at)
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
