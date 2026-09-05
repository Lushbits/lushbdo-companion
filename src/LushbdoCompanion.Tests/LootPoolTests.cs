using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The pool's one promise: loot picked up while the session was not live
/// never reaches it — not from before Start, not from a break. Every cut it
/// makes is on time — the instant a post went out that the site answered
/// not-live, and the instant a live answer says the session went live — so
/// every test here is a timeline.
/// </summary>
public class LootPoolTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 19, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    private readonly LootPool _pool = new();

    /// <summary>Mint after the drops have settled — the ordinary way a parcel comes to be.</summary>
    private LootPool.Parcel MintSettled(double lastAddSeconds) =>
        _pool.Mint(At(lastAddSeconds) + LootPool.CoalesceWindow)!;

    [Fact]
    public void SameNamesMergeIntoOneLineAndTheParcelIsTheSameUntilItResolves()
    {
        _pool.Add("Rough Stone", 1, At(0));
        _pool.Add("Weeds", 3, At(0.1));
        _pool.Add("Rough Stone", 2, At(0.2));

        var parcel = MintSettled(0.2);
        Assert.Equal([("Rough Stone", 3), ("Weeds", 3)], parcel.Lines);
        Assert.Equal(3, parcel.Pickups.Count);
        Assert.StartsWith("companion-", parcel.Id);

        // A redelivery is the same parcel with the same id, which is what the
        // site's ring keys on.
        Assert.Same(parcel, _pool.Mint(At(30)));
        Assert.Equal(3, _pool.Count);
    }

    [Fact]
    public void TheParcelWaitsForQuietButNotForever()
    {
        _pool.Add("Weeds", 1, At(0));
        Assert.Null(_pool.Mint(At(0.2)));                 // still coalescing
        _pool.Add("Weeds", 1, At(0.3));
        Assert.Null(_pool.Mint(At(0.5)));                 // the burst is still going
        Assert.NotNull(_pool.Mint(At(0.3) + LootPool.CoalesceWindow));

        var busy = new LootPool();
        for (var i = 0; i < 30; i++) busy.Add("Weeds", 1, At(i * 0.1));
        Assert.Null(busy.Mint(At(2.95)));                 // never quiet …
        Assert.NotNull(busy.Mint(At(3.05)));              // … but waited long enough
    }

    [Fact]
    public void NotLiveDropsTheParcelAndEverythingSeenBeforeItWasSent()
    {
        _pool.Add("Rough Stone", 1, At(0));
        var parcel = MintSettled(0);
        _pool.Add("Weeds", 1, At(5));                     // pooled while the parcel waits
        _pool.Add("Rough Stone", 1, At(10.2));            // arrived during the round trip

        var dropped = _pool.NotLive(sentAt: At(10));
        Assert.Equal(2, dropped);
        Assert.Equal(1, _pool.Count);                     // the round-trip arrival is the one survivor
        Assert.True(_pool.Probing);

        // The old parcel is gone; the survivor becomes the next one.
        var next = MintSettled(10.2);
        Assert.NotSame(parcel, next);
        Assert.Equal([("Rough Stone", 1)], next.Lines);
    }

    [Fact]
    public void AnEmptyPostThatIsAnsweredNotLiveCutsThePoolTheSameWay()
    {
        // The sender asks with no lines while the session is not live, so
        // there is no parcel — the cut is on the pool alone.
        _pool.NotLive(At(0));
        _pool.Add("Weeds", 1, At(3));
        _pool.Add("Weeds", 1, At(14));
        _pool.Add("Weeds", 1, At(15.1));

        Assert.Equal(2, _pool.NotLive(sentAt: At(15)));
        Assert.Equal(1, _pool.Count);
        Assert.True(_pool.Probing);
    }

    [Fact]
    public void WhileProbingTheNextParcelIsOneOldestPickup()
    {
        _pool.NotLive(At(0));
        _pool.Add("Rough Stone", 1, At(1));
        _pool.Add("Weeds", 2, At(1.1));
        _pool.Add("Rough Stone", 1, At(1.2));

        var probe = MintSettled(1.2);
        Assert.Equal([("Rough Stone", 1)], probe.Lines);
        Assert.Equal(At(1), Assert.Single(probe.Pickups).SeenAt);
        Assert.Equal(3, _pool.Count);                     // the other two still wait
    }

    [Fact]
    public void LiveCutsWhatWasSeenBeforeTheSessionWentLive()
    {
        _pool.NotLive(At(0));
        _pool.Add("Rough Stone", 1, At(2));               // the probe
        _pool.Add("Weeds", 1, At(5));                     // before Start
        _pool.Add("Weeds", 1, At(8));                     // before Start
        _pool.Add("Rough Stone", 1, At(12));              // after Start
        var probe = MintSettled(12);
        Assert.Single(probe.Pickups);

        // The site answers at 15 s that the session went live 5 s ago.
        var (rode, pruned) = _pool.Live(liveSince: At(10));
        Assert.Equal(1, rode);                            // the probe itself predated Start, and says so
        Assert.Equal(2, pruned);
        Assert.False(_pool.Probing);

        var rest = _pool.Mint(At(15.1));                  // no longer a probe: everything left goes
        Assert.NotNull(rest);
        Assert.Equal([("Rough Stone", 1)], rest!.Lines);
        Assert.Equal(At(12), Assert.Single(rest.Pickups).SeenAt);
    }

    [Fact]
    public void ABreaksLootNeverReachesTheResumedRun()
    {
        // Paused at 100: the post at 105 is answered `paused`, and the member
        // logs an alt to kill a boss. Resume at 300. The sender asked with an
        // empty post at 305 and was told the session went live 5 s ago.
        _pool.Add("Weeds", 1, At(101));
        MintSettled(101);
        Assert.Equal(1, _pool.NotLive(sentAt: At(105)));

        _pool.Add("Boss Drop", 1, At(200));
        _pool.Add("Boss Drop", 1, At(290));
        _pool.Add("Weeds", 1, At(301));                   // the run's own, after Resume

        var (rode, pruned) = _pool.Live(liveSince: At(300));
        Assert.Equal(0, rode);                            // nothing was posted to find out
        Assert.Equal(2, pruned);
        Assert.Equal(1, _pool.Count);
        Assert.Equal([("Weeds", 1)], _pool.Mint(At(305.5))!.Lines);
    }

    [Fact]
    public void LiveReportsNothingWhenEverythingWasInsideTheSession()
    {
        _pool.Add("Weeds", 1, At(20));
        _pool.Add("Weeds", 1, At(21));
        MintSettled(21);
        _pool.Add("Weeds", 1, At(22));

        Assert.Equal((0, 0), _pool.Live(liveSince: At(10)));
        Assert.Equal(1, _pool.Count);
    }

    [Fact]
    public void ALiveAnswerThatNamesNoInstantCutsNothing()
    {
        _pool.NotLive(At(0));
        _pool.Add("Weeds", 1, At(1));
        MintSettled(1);
        _pool.Add("Weeds", 1, At(2));

        Assert.Equal((0, 0), _pool.Live(liveSince: null));
        Assert.Equal(1, _pool.Count);
        Assert.False(_pool.Probing);
    }

    [Fact]
    public void ARestartedSessionCutsPendingLootFromBetweenTheRuns()
    {
        // Flowing: a batch lands, then the member stops and starts a new run
        // while the next batch is pooling. Everything seen before the new Start
        // belongs to no run.
        _pool.Add("Weeds", 1, At(0));
        MintSettled(0);
        _pool.Live(liveSince: At(-60));

        _pool.Add("Weeds", 1, At(3));
        _pool.Add("Weeds", 1, At(4));
        MintSettled(4);
        _pool.Add("Weeds", 1, At(9));                     // between the runs, still pooled
        _pool.Add("Weeds", 1, At(9.5));                   // inside the new run

        var (rode, pruned) = _pool.Live(liveSince: At(9.2));
        Assert.Equal(2, rode);
        Assert.Equal(1, pruned);
        Assert.Equal(1, _pool.Count);
    }
}
