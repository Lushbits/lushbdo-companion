using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The board's one promise, exercised from every direction: a physical line
/// emits exactly once, only after a parseable reading recurs, and every
/// ambiguity lands on the undercount side — never a double count.
/// </summary>
public class LineBoardTests
{
    private const string RoughStone = "You have obtained [Rough Stone]. (18:44)";
    private const string Weeds = "You have obtained [Weeds] x3. (18:45)";

    private readonly List<(string Name, int Count, string Raw)> _emitted = [];
    private readonly List<string> _notes = [];
    private readonly LineBoard _board;

    public LineBoardTests()
    {
        _board = new LineBoard((n, c, raw) => _emitted.Add((n, c, raw)), _notes.Add);
    }

    private void Pass(params (string Text, double Y)[] lines) =>
        _board.Ingest(lines.Select(l => new LineBoard.OcrLineInput(l.Text, l.Y, 16)).ToList());

    [Fact]
    public void LinesVisibleAtStartAreBaselineAndNeverSend()
    {
        Pass((RoughStone, 100), (Weeds, 120));
        Pass((RoughStone, 100), (Weeds, 120));
        Pass((RoughStone, 100), (Weeds, 120));
        Assert.Empty(_emitted);
        Assert.Contains(_notes, n => n.Contains("Baseline"));
    }

    [Fact]
    public void ANewLineEmitsOnceAfterItsReadingRecurs()
    {
        Pass(); // empty baseline
        Pass((RoughStone, 100));
        Assert.Empty(_emitted); // one frame's word is never enough
        Pass((RoughStone, 100));
        Assert.Equal([("Rough Stone", 1, RoughStone)], _emitted);
        Pass((RoughStone, 100));
        Pass((RoughStone, 100));
        Assert.Single(_emitted); // settled means done — no re-emission, ever
    }

    [Fact]
    public void IdenticalAdjacentLinesAreDistinctPickups()
    {
        // The exact case content-based dedup can never solve (bdo#581).
        Pass();
        Pass((RoughStone, 100), (RoughStone, 120));
        Pass((RoughStone, 100), (RoughStone, 120));
        Assert.Equal(2, _emitted.Count);
        Assert.All(_emitted, e => Assert.Equal("Rough Stone", e.Name));
    }

    [Fact]
    public void ScrollIsMeasuredFromStableTextAndOnlyTheNewLineEmits()
    {
        Pass((RoughStone, 100)); // baseline
        // The chat scrolled up 18px and Weeds appeared at the bottom.
        Pass((RoughStone, 82), (Weeds, 100));
        Pass((RoughStone, 82), (Weeds, 100));
        Assert.Equal([("Weeds", 3, Weeds)], _emitted);
    }

    [Fact]
    public void ReconfirmSettlesALineWithoutASecondOcrPass()
    {
        Pass();
        Pass((RoughStone, 100));
        _board.Reconfirm(); // stabilized image unchanged → the reading holds
        Assert.Equal([("Rough Stone", 1, RoughStone)], _emitted);
        _board.Reconfirm();
        Assert.Single(_emitted);
    }

    [Fact]
    public void MisreadsLoseTheVoteToTheRecurringTruth()
    {
        Pass();
        Pass(("You have obtajned [Rough 5tone]. (18:44)", 100)); // a mangled frame
        Pass((RoughStone, 100));
        Pass((RoughStone, 100));
        Assert.Equal([("Rough Stone", 1, RoughStone)], _emitted);
    }

    [Fact]
    public void WrappedNameJoinsItsQuantityTail()
    {
        const string head = "You have obtained [Secret Book of the Forgotten Adventurer]";
        const string tail = "x4. (18:51)";
        Pass();
        Pass((head, 100), (tail, 118));
        Pass((head, 100), (tail, 118));
        var e = Assert.Single(_emitted);
        Assert.Equal("Secret Book of the Forgotten Adventurer", e.Name);
        Assert.Equal(4, e.Count);
    }

    [Fact]
    public void WrappedTimestampTailIsConsumedSilently()
    {
        const string head = "You have obtained e [Concentrated Magical Black Gem] x100.";
        Pass();
        Pass((head, 100), ("(19:33)", 118));
        Pass((head, 100), ("(19:33)", 118));
        var e = Assert.Single(_emitted);
        Assert.Equal("Concentrated Magical Black Gem", e.Name);
        Assert.Equal(100, e.Count);
        Assert.DoesNotContain(_notes, n => n.Contains("(19:33)"));
    }

    [Fact]
    public void SilverIsSkippedAloudNotSent()
    {
        const string silver = "You have obtained [Silver] x995,374. (19:00)";
        Pass();
        Pass((silver, 100));
        Pass((silver, 100));
        Assert.Empty(_emitted);
        Assert.Contains(_notes, n => n.Contains("silver"));
    }

    [Fact]
    public void UnparseableLinesAreSkippedAloudWhenTheyLeave()
    {
        Pass((RoughStone, 100)); // baseline anchor keeps alignment alive
        Pass((RoughStone, 100), ("Guildmate: hello there", 120));
        for (var i = 0; i < 7; i++) Pass((RoughStone, 100)); // the chat line fades out
        Assert.Empty(_emitted);
        Assert.Contains(_notes, n => n.Contains("Guildmate") && n.Contains("skip"));
    }

    [Fact]
    public void ThreeBlindPassesRealignAndWhatFollowsIsOld()
    {
        Pass((RoughStone, 100)); // baseline
        Pass(("~~~~", 100));     // a storm of mangled frames
        Pass(("≈≈≈≈", 100));
        Pass(("∞∞∞∞", 100));
        Assert.Contains(_notes, n => n.Contains("Realigning"));
        // The storm passes; what is visible could be lines already counted.
        Pass((RoughStone, 82), (Weeds, 100));
        Pass((RoughStone, 82), (Weeds, 100));
        Pass((RoughStone, 82), (Weeds, 100));
        Assert.Empty(_emitted);
    }

    [Fact]
    public void BackwardsScrollRealignsInsteadOfRecounting()
    {
        Pass((RoughStone, 40)); // baseline
        Pass((RoughStone, 40));
        // The member wheel-scrolled the tab: everything moved down, and lines
        // we may have already counted are "revealed" below. One pass saying so
        // could be a loot burst's duplicate votes lying (the burst tests), so
        // the board holds fire; a second pass still backwards is believed —
        // and then never recount.
        Pass((RoughStone, 90), (Weeds, 110));
        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
        Assert.Empty(_emitted);
        Pass((RoughStone, 90), (Weeds, 110));
        Assert.Contains(_notes, n => n.Contains("Realigning"));
        Pass((RoughStone, 90), (Weeds, 110));
        Pass((RoughStone, 90), (Weeds, 110));
        Assert.Empty(_emitted);
    }

    [Fact]
    public void OneBackwardsVoteHoldsFireAndPlayResumes()
    {
        Pass((RoughStone, 40)); // baseline
        Pass((RoughStone, 40));
        Pass((RoughStone, 90)); // one pass says everything moved down…
        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
        // …but the next read is back where the board left it, plus a pickup.
        // A held board resumes exactly where it was; nothing was dumped.
        Pass((RoughStone, 40), (Weeds, 60));
        Pass((RoughStone, 40), (Weeds, 60));
        Assert.Equal([("Weeds", 3, Weeds)], _emitted);
        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
    }

    [Fact]
    public void ABurstOfNearIdenticalDropsIsNotMistakenForBackwardsScroll()
    {
        // Sea-monster looting: 10–15 near-identical rows land in a gulp —
        // same items, same counts, same minute timestamp. Duplicate text
        // matches between them are periodic, voting coherently for a small
        // *downward* shift; they must not out-vote the unique survivors that
        // pin the true upward one, or the whole burst is dumped as "the chat
        // scrolled backwards" (the field log's 23-lines-skipped realign).
        const string pouch = "You have obtained [Sea Monster's Spirit Pouch] x19. (20:19)";
        const string skin = "You have obtained [Young Ocean Stalker's Skin] x16. (20:19)";
        const string plywood = "You have obtained [Island Tree Coated Plywood] x3. (20:19)";
        const string salt = "You have obtained [Rock Salt Ingot] x2. (20:19)";

        Pass((pouch, 0), (skin, 20), (salt, 40), (skin, 60), (plywood, 80)); // baseline
        Pass((pouch, 0), (skin, 20), (salt, 40), (skin, 60), (plywood, 80));
        // Three drops land at once: the chat scrolls up 60px, the two bottom
        // lines survive, and pouch/skin copies fill the space below them.
        Pass((skin, 0), (plywood, 20), (pouch, 40), (skin, 60), (pouch, 80));
        Pass((skin, 0), (plywood, 20), (pouch, 40), (skin, 60), (pouch, 80));

        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
        Assert.Equal(3, _emitted.Count);
        Assert.Equal(2, _emitted.Count(e => e is ("Sea Monster's Spirit Pouch", 19, _)));
        Assert.Equal(1, _emitted.Count(e => e is ("Young Ocean Stalker's Skin", 16, _)));
    }

    [Fact]
    public void NewCopiesOfLinesAlreadyOnScreenAreCountedNotSwallowed()
    {
        // The other face of duplicate voting: when it drags the shift to
        // "no scroll", fresh drops identical to lines above merge into those
        // lines' trackers and vanish — no count, no skip note, nothing.
        const string pouch = "You have obtained [Sea Monster's Spirit Pouch] x19. (20:19)";
        const string plywood = "You have obtained [Island Tree Coated Plywood] x3. (20:19)";
        const string salt = "You have obtained [Rock Salt Ingot] x2. (20:19)";

        Pass((salt, 0), (pouch, 20), (pouch, 40), (plywood, 60)); // baseline
        Pass((salt, 0), (pouch, 20), (pouch, 40), (plywood, 60));
        // Three more pouches in one gulp: everything shifts up 60px.
        Pass((plywood, 0), (pouch, 20), (pouch, 40), (pouch, 60));
        Pass((plywood, 0), (pouch, 20), (pouch, 40), (pouch, 60));

        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
        Assert.Equal(3, _emitted.Count);
        Assert.All(_emitted, e => Assert.Equal(("Sea Monster's Spirit Pouch", 19), (e.Name, e.Count)));
    }

    [Fact]
    public void NameWrappedMidBracketJoinsItsOtherHalf()
    {
        // The wrap can land inside the bracket (field screenshot, 20:25):
        // the head never closes it and the rest arrives as the next line.
        const string head = "You have obtained [Deep Tide-Dyed Standardized Timber";
        const string tail = "Square] x4. (20:25)";
        Pass();
        Pass((head, 100), (tail, 118));
        Pass((head, 100), (tail, 118));
        var e = Assert.Single(_emitted);
        Assert.Equal("Deep Tide-Dyed Standardized Timber Square", e.Name);
        Assert.Equal(4, e.Count);
    }

    [Fact]
    public void AWrappedNameWhoseEndingNeverArrivesIsSkippedAloud()
    {
        const string head = "You have obtained [Deep Tide-Dyed Standardized Timber";
        Pass();
        Pass((head, 100), (Weeds, 120)); // the next line is a full message, not the name's rest
        Pass((head, 100), (Weeds, 120));
        Assert.Equal([("Weeds", 3, Weeds)], _emitted);
        Assert.Contains(_notes, n => n.Contains("ending never arrived"));
    }

    [Fact]
    public void AWrappedHeadWhoseTailNeverArrivesIsSkippedAloud()
    {
        const string head = "You have obtained [Secret Book of the Forgotten Adventurer]";
        Pass();
        Pass((head, 100), (Weeds, 120)); // the next line is a full message, not a tail
        Pass((head, 100), (Weeds, 120));
        Assert.Equal([("Weeds", 3, Weeds)], _emitted);
        Assert.Contains(_notes, n => n.Contains("quantity never arrived"));
    }
}
