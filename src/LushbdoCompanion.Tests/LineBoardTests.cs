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
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 82), (RoughStone, 100)); // the chat scrolls up; the pickup enters at the bottom
        Assert.Empty(_emitted); // one frame's word is never enough
        Pass((Weeds, 82), (RoughStone, 100));
        Assert.Equal([("Rough Stone", 1, RoughStone)], _emitted);
        Pass((Weeds, 82), (RoughStone, 100));
        Pass((Weeds, 82), (RoughStone, 100));
        Assert.Single(_emitted); // settled means done — no re-emission, ever
    }

    [Fact]
    public void IdenticalAdjacentLinesAreDistinctPickups()
    {
        // The exact case content-based dedup can never solve (bdo#581).
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 60), (RoughStone, 80), (RoughStone, 100)); // two identical drops in one gulp
        Pass((Weeds, 60), (RoughStone, 80), (RoughStone, 100));
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
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 82), (RoughStone, 100));
        _board.Reconfirm(); // stabilized image unchanged → the reading holds
        Assert.Equal([("Rough Stone", 1, RoughStone)], _emitted);
        _board.Reconfirm();
        Assert.Single(_emitted);
    }

    [Fact]
    public void MisreadsLoseTheVoteToTheRecurringTruth()
    {
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 82), ("You have obtajned [Rough 5tone]. (18:44)", 100)); // a mangled frame
        Pass((Weeds, 82), (RoughStone, 100));
        Pass((Weeds, 82), (RoughStone, 100));
        Assert.Equal([("Rough Stone", 1, RoughStone)], _emitted);
    }

    [Fact]
    public void WrappedNameJoinsItsQuantityTail()
    {
        const string head = "You have obtained [Secret Book of the Forgotten Adventurer]";
        const string tail = "x4. (18:51)";
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 64), (head, 82), (tail, 100)); // the wrapped pickup is two visual rows
        Pass((Weeds, 64), (head, 82), (tail, 100));
        var e = Assert.Single(_emitted);
        Assert.Equal("Secret Book of the Forgotten Adventurer", e.Name);
        Assert.Equal(4, e.Count);
    }

    [Fact]
    public void WrappedTimestampTailIsConsumedSilently()
    {
        const string head = "You have obtained e [Concentrated Magical Black Gem] x100.";
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 64), (head, 82), ("(19:33)", 100));
        Pass((Weeds, 64), (head, 82), ("(19:33)", 100));
        var e = Assert.Single(_emitted);
        Assert.Equal("Concentrated Magical Black Gem", e.Name);
        Assert.Equal(100, e.Count);
        Assert.DoesNotContain(_notes, n => n.Contains("(19:33)"));
    }

    [Fact]
    public void SilverIsSkippedAloudNotSent()
    {
        const string silver = "You have obtained [Silver] x995,374. (19:00)";
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 82), (silver, 100));
        Pass((Weeds, 82), (silver, 100));
        Assert.Empty(_emitted);
        Assert.Contains(_notes, n => n.Contains("silver"));
    }

    [Fact]
    public void UnparseableLinesAreSkippedAloudWhenTheyLeave()
    {
        Pass((RoughStone, 100)); // baseline anchor keeps alignment alive
        Pass((RoughStone, 82), ("Guildmate: hello there", 100));
        for (var i = 0; i < 7; i++) Pass((RoughStone, 82)); // the chat line fades out
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
        // …but the next read scrolls on from where the board left it, with a
        // pickup entering at the bottom. A held board resumes; nothing was
        // dumped.
        Pass((RoughStone, 22), (Weeds, 40));
        Pass((RoughStone, 22), (Weeds, 40));
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
    public void UniqueLinesOverruleDuplicatesFakingABackwardsScroll()
    {
        // Identical lines stacked a row apart vote coherently for their own
        // spacing — enough, in a heavy burst with the survivors mangled, to
        // win the full vote with a small downward shift twice in a row and
        // two-strike the persistence gate (field log, 20:58:35: 12
        // unconfirmed seal lines dumped). The texts that cannot alias —
        // visible once, tracked once — arbitrate: they pin the true shift,
        // and the burst is counted, not dumped.
        const string pouch = "You have obtained [Sea Monster's Spirit Pouch] x19. (20:19)";
        const string skin = "You have obtained [Young Ocean Stalker's Skin] x16. (20:19)";
        const string plywood = "You have obtained [Island Tree Coated Plywood] x3. (20:19)";
        const string salt = "You have obtained [Rock Salt Ingot] x2. (20:19)";

        Pass((pouch, 0), (skin, 20), (salt, 40), (skin, 60), (plywood, 80)); // baseline
        Pass((pouch, 0), (skin, 20), (salt, 40), (skin, 60), (plywood, 80));
        // Four drops in one gulp: the chat scrolls up 80px, only the bottom
        // line survives, and the new pouch/skin pairs repeat the pattern.
        Pass((plywood, 0), (pouch, 20), (skin, 40), (pouch, 60), (skin, 80));
        Pass((plywood, 0), (pouch, 20), (skin, 40), (pouch, 60), (skin, 80));

        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
        Assert.Equal(4, _emitted.Count);
        Assert.Equal(2, _emitted.Count(e => e is ("Sea Monster's Spirit Pouch", 19, _)));
        Assert.Equal(2, _emitted.Count(e => e is ("Young Ocean Stalker's Skin", 16, _)));
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
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 64), (head, 82), (tail, 100));
        Pass((Weeds, 64), (head, 82), (tail, 100));
        var e = Assert.Single(_emitted);
        Assert.Equal("Deep Tide-Dyed Standardized Timber Square", e.Name);
        Assert.Equal(4, e.Count);
    }

    [Fact]
    public void AWrappedNameWhoseEndingNeverArrivesIsSkippedAloud()
    {
        const string head = "You have obtained [Deep Tide-Dyed Standardized Timber";
        const string salt = "You have obtained [Rock Salt Ingot] x2. (20:19)";
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 64), (head, 82), (salt, 100)); // the next line is a full message, not the name's rest
        Pass((Weeds, 64), (head, 82), (salt, 100));
        Assert.Equal([("Rock Salt Ingot", 2, salt)], _emitted);
        Assert.Contains(_notes, n => n.Contains("ending never arrived"));
    }

    [Fact]
    public void AWrappedHeadWhoseTailNeverArrivesIsSkippedAloud()
    {
        const string head = "You have obtained [Secret Book of the Forgotten Adventurer]";
        const string salt = "You have obtained [Rock Salt Ingot] x2. (20:19)";
        Pass((Weeds, 100)); // baseline anchor
        Pass((Weeds, 64), (head, 82), (salt, 100)); // the next line is a full message, not a tail
        Pass((Weeds, 64), (head, 82), (salt, 100));
        Assert.Equal([("Rock Salt Ingot", 2, salt)], _emitted);
        Assert.Contains(_notes, n => n.Contains("quantity never arrived"));
    }

    [Fact]
    public void ReappearingOldRowsNeverReEmit()
    {
        // The transparent chat washes out row by row against a bright
        // world; a realign re-baselines on whatever rows stay readable.
        // When the camera turns and contrast returns, the old rows
        // materialize mid-screen without anything scrolling — and must
        // never count again (field log, 21:15:32 and 21:17:05: the same ×3
        // row was sent three times this way).
        const string oldA = "You have obtained [Sea Monster's Ooze] x36. (20:57)";
        const string oldB = "You have obtained [Cox Pirates Extermination Seal] x3. (21:13)";
        Pass((RoughStone, 60), (Weeds, 80)); // the crippled baseline: what a faded screen still reads as
        Pass((RoughStone, 60), (Weeds, 80));
        // The chat un-fades: old rows appear above and below; nothing scrolls.
        Pass((oldA, 20), (oldB, 40), (RoughStone, 60), (Weeds, 80), (oldB, 100), (oldA, 120));
        Pass((oldA, 20), (oldB, 40), (RoughStone, 60), (Weeds, 80), (oldB, 100), (oldA, 120));
        Pass((oldA, 20), (oldB, 40), (RoughStone, 60), (Weeds, 80), (oldB, 100), (oldA, 120));
        Assert.Empty(_emitted);
        Assert.Contains(_notes, n => n.Contains("revealed"));
    }

    [Fact]
    public void ARevealBeneathTheBottomCannotRideAnArrival()
    {
        // A crippled baseline can miss the bottom rows too. When a real
        // pickup then arrives — content moves up one row — the reveal below
        // the old bottom must not slip in with it: one row of scroll admits
        // exactly one new line, the bottom-most.
        const string oldB = "You have obtained [Cox Pirates Extermination Seal] x3. (21:13)";
        Pass((RoughStone, 40), (Weeds, 60)); // baseline: the readable top of a fading screen
        Pass((RoughStone, 40), (Weeds, 60));
        // One real pickup enters at the bottom (everything shifts up 20) and
        // the un-fade reveals two old rows beneath the old bottom edge.
        Pass((RoughStone, 20), (Weeds, 40), (oldB, 60), (oldB, 80), (RoughStone, 100));
        Pass((RoughStone, 20), (Weeds, 40), (oldB, 60), (oldB, 80), (RoughStone, 100));
        Pass((RoughStone, 20), (Weeds, 40), (oldB, 60), (oldB, 80), (RoughStone, 100));
        var e = Assert.Single(_emitted);
        Assert.Equal("Rough Stone", e.Name); // the bottom-most line is the arrival
    }

    [Fact]
    public void ALineSeenBeforeItsScrollIsMeasuredIsStillCounted()
    {
        // A mid-flip median can show the new bottom line one pass before
        // the survivors' shift reads (field log, 22:12: nearly every real
        // pickup was skipped as "revealed"). Seeing it early must not
        // condemn it — it waits untracked until a pass measures the motion,
        // then counts.
        const string blood = "You have obtained [Ox Blood] x8. (22:11)";
        Pass((RoughStone, 60), (Weeds, 80)); // baseline
        Pass((RoughStone, 60), (Weeds, 80), (blood, 100)); // the line reads before the scroll does
        Pass((RoughStone, 40), (Weeds, 60), (blood, 80));  // the scroll is measured — it enters properly
        Pass((RoughStone, 40), (Weeds, 60), (blood, 80));
        Assert.Equal([("Ox Blood", 8, blood)], _emitted);
        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
    }

    [Fact]
    public void AWashedOutChatHoldsAndResumesInsteadOfRealigning()
    {
        // The transparent chat washes out against a bright world (or
        // another window covers it) and OCR goes near-blind. Blindness is
        // not a different screen: the board holds its trackers through the
        // blind spell, and the returning text matches them again — old rows
        // recognized, and the pickup that arrived meanwhile entering at the
        // bottom.
        const string salt = "You have obtained [Rock Salt Ingot] x2. (20:19)";
        const string blood = "You have obtained [Ox Blood] x8. (22:11)";
        Pass((RoughStone, 40), (Weeds, 60), (salt, 80)); // baseline
        Pass(("~~", 50));   // the fade: fragments only
        Pass(("≈", 60));
        Pass(("~~", 50));
        Pass(("≈≈", 40));
        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
        Assert.Contains(_notes, n => n.Contains("holding"));
        // The chat un-fades with one new pickup at the bottom.
        Pass((RoughStone, 20), (Weeds, 40), (salt, 60), (blood, 80));
        Pass((RoughStone, 20), (Weeds, 40), (salt, 60), (blood, 80));
        Assert.Equal([("Ox Blood", 8, blood)], _emitted);
        Assert.DoesNotContain(_notes, n => n.Contains("Realigning"));
    }

    [Fact]
    public void ABlankReadNeverCompletesABaseline()
    {
        // A fully faded chat (or a loading screen) reads as nothing. Nothing
        // is no anchor: what appears afterwards is old content revealing
        // itself, and the baseline waits for it.
        Pass(); // OCR read nothing
        Pass((RoughStone, 82), (Weeds, 100)); // content appears: old, not pickups
        Pass((RoughStone, 82), (Weeds, 100));
        Pass((RoughStone, 82), (Weeds, 100));
        Assert.Empty(_emitted);
        Assert.Contains(_notes, n => n.Contains("Baseline"));
    }
}
