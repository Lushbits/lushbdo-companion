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
        // we may have already counted are "revealed" below. Never recount.
        Pass((RoughStone, 90), (Weeds, 110));
        Assert.Contains(_notes, n => n.Contains("Realigning"));
        Pass((RoughStone, 90), (Weeds, 110));
        Pass((RoughStone, 90), (Weeds, 110));
        Assert.Empty(_emitted);
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
