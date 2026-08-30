using LushbdoCompanion;
using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The stillness gate and the confirm/hold rules (#22). Every case here is
/// about the same thing from a different side: a figure the app is not sure of
/// is never confirmed, and the cost of being unsure is a stale number rather
/// than a wrong one.
/// </summary>
public class BalanceBoardTests
{
    private const int Length = 64; // 4×4 px of BGRA — enough for MeanAbsDiff to sample

    private readonly List<string> _notes = [];

    private BalanceBoard NewBoard() => new(_notes.Add);

    private static byte[] Picture(byte fill)
    {
        var pixels = new byte[Length];
        Array.Fill(pixels, fill);
        return pixels;
    }

    /// <summary>The first frame of a picture is only a baseline; the second is when stillness is knowable.</summary>
    private static void Settle(BalanceBoard board, byte[] picture) => board.Observe(picture, Length);

    private static void Read(BalanceBoard board, byte[] picture, string text)
    {
        Assert.True(board.Observe(picture, Length), "the gate should have wanted this read");
        board.TakeRead();
        board.Ingest(text);
    }

    [Fact]
    public void TheFirstFrameIsOnlyABaseline()
    {
        var board = NewBoard();
        Assert.False(board.Observe(Picture(50), Length));
    }

    /// <summary>
    /// Moving pixels are the world, not a panel — and the world is most of
    /// every session, so this is the case that has to cost nothing.
    /// </summary>
    [Fact]
    public void MovingPixelsAreNeverRead()
    {
        var board = NewBoard();
        for (var i = 0; i < 10; i++)
            Assert.False(board.Observe(Picture((byte)(i * 20)), Length));
        Assert.Equal(0, board.Reads);
    }

    [Fact]
    public void ConfirmsOnlyAfterThreeAgreeingReadings()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);

        Read(board, picture, "Warehouse Balance 1,234,567");
        Assert.Null(board.Confirmed);
        Read(board, picture, "Warehouse Balance 1,234,567");
        Assert.Null(board.Confirmed);
        Read(board, picture, "Warehouse Balance 1,234,567");

        Assert.Equal(1234567L, board.Confirmed);
        Assert.Contains(_notes, n => n.Contains("confirmed 1,234,567"));
    }

    /// <summary>
    /// Readings that disagree are a recognizer that is not sure, and a balance
    /// has nothing downstream to catch a plausible wrong figure.
    /// </summary>
    [Fact]
    public void DisagreeingReadingsNeverConfirm()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);

        for (var i = 0; i < BalanceBoard.ReadsPerPicture; i++)
            Read(board, picture, i % 2 == 0 ? "Warehouse Balance 1,234,567" : "Warehouse Balance 7,654,321");

        Assert.Null(board.Confirmed);
        Assert.Contains(_notes, n => n.Contains("without 3 agreeing readings"));
    }

    /// <summary>A shape the parser refuses is reported once, not once per pass.</summary>
    [Fact]
    public void ARefusedShapeIsExplainedOnce()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);

        for (var i = 0; i < BalanceBoard.ReadsPerPicture; i++)
            Read(board, picture, "Warehouse Balance 1,00");

        Assert.Null(board.Confirmed);
        Assert.Single(_notes);
        Assert.Contains("1,00", _notes[0]);
    }

    /// <summary>
    /// A rectangle that keeps confirming the same figure says so periodically
    /// rather than going silent. Silence was read as breakage twice in one
    /// session (2026-08-30), which is all the evidence that rule needed.
    /// </summary>
    [Fact]
    public void ARectangleStillReadingTheSameFigureSaysSoEventually()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);
        for (var i = 0; i < BalanceBoard.AgreeingReads; i++) Read(board, picture, "Warehouse Balance 1,000,000");
        Assert.Single(_notes);

        // The panel drifts and re-confirms shortly after: still quiet.
        var drifted = Picture(120);
        board.Observe(drifted, Length);
        Read(board, drifted, "Warehouse Balance 1,000,000");
        Assert.Single(_notes);

        // Once the repeat window has passed, it says it is still reading it.
        for (var i = 0; i < BalanceBoard.RepeatNoteTicks; i++) board.Observe(drifted, Length);
        var again = Picture(200);
        board.Observe(again, Length);
        Read(board, again, "Warehouse Balance 1,000,000");

        Assert.Equal(2, _notes.Count);
        Assert.Contains("still reading 1,000,000", _notes[1]);
    }

    /// <summary>
    /// A confirmed picture is finished with. Re-reading it would be the same
    /// arithmetic over the same pixels, and the CPU belongs to the game.
    /// </summary>
    [Fact]
    public void AConfirmedPictureIsNotReadAgain()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);
        for (var i = 0; i < BalanceBoard.AgreeingReads; i++) Read(board, picture, "Warehouse Balance 1,000,000");

        var before = board.Reads;
        for (var i = 0; i < 20; i++) Assert.False(board.Observe(picture, Length));
        Assert.Equal(before, board.Reads);
    }

    /// <summary>...but a figure that changed is a new question, and is read again.</summary>
    [Fact]
    public void AChangedPictureIsReadAgain()
    {
        var board = NewBoard();
        var first = Picture(50);
        Settle(board, first);
        for (var i = 0; i < BalanceBoard.AgreeingReads; i++) Read(board, first, "Warehouse Balance 1,000,000");

        var second = Picture(120);
        Assert.False(board.Observe(second, Length)); // it moved to get here
        for (var i = 0; i < BalanceBoard.AgreeingReads; i++) Read(board, second, "Warehouse Balance 2,000,000");

        Assert.Equal(2_000_000L, board.Confirmed);
    }

    /// <summary>
    /// A rectangle left over a static piece of scenery reads badly forever;
    /// the cap is what stops that being a pass a second for the whole session.
    /// </summary>
    [Fact]
    public void OnePictureIsWorthAtMostSixPasses()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);

        for (var i = 0; i < BalanceBoard.ReadsPerPicture; i++) Read(board, picture, "nothing here at all");
        for (var i = 0; i < 50; i++) Assert.False(board.Observe(picture, Length));
        Assert.Equal(BalanceBoard.ReadsPerPicture, board.Reads);
    }

    /// <summary>
    /// The bug the first field trace caught (2026-08-30 15:45): the panel
    /// drifts between reads, so every read arrived under a "new picture" and
    /// the vote restarted at one. The real figure was read correctly five
    /// times and confirmed none of them. Agreement belongs to the reading, not
    /// to the pixels.
    /// </summary>
    [Fact]
    public void APanelThatDriftsBetweenReadsStillConfirms()
    {
        var board = NewBoard();
        const string field = "Warehouse Balance 23,975,827,939";

        for (var i = 0; i < BalanceBoard.AgreeingReads; i++)
        {
            // Each read sits on its own slightly different picture, with the
            // world moving in between — exactly the shape of the trace.
            var drifted = Picture((byte)(60 + i * 30));
            Assert.False(board.Observe(drifted, Length)); // it moved to get here
            Read(board, drifted, field);
        }

        Assert.Equal(23_975_827_939L, board.Confirmed);
    }

    /// <summary>Drift is not a licence to agree with itself: different readings still never confirm.</summary>
    [Fact]
    public void DriftDoesNotLetDisagreeingReadingsConfirm()
    {
        var board = NewBoard();

        for (var i = 0; i < 9; i++)
        {
            var drifted = Picture((byte)(20 + i * 20));
            board.Observe(drifted, Length);
            if (board.Observe(drifted, Length))
            {
                board.TakeRead();
                board.Ingest(i % 2 == 0 ? "Warehouse Balance 1,234,567" : "Warehouse Balance 7,654,321");
            }
        }

        Assert.Null(board.Confirmed);
    }

    /// <summary>
    /// The frames stopped: the confirmed figure is the log's memory and stands,
    /// but nothing that was half-agreed carries across the gap.
    /// </summary>
    [Fact]
    public void ResetKeepsTheConfirmedFigureAndDropsThePendingOne()
    {
        var board = NewBoard();
        var picture = Picture(50);
        Settle(board, picture);
        for (var i = 0; i < BalanceBoard.AgreeingReads; i++) Read(board, picture, "Warehouse Balance 1,000,000");

        var other = Picture(120);
        Assert.False(board.Observe(other, Length));
        Read(board, other, "Warehouse Balance 2,000,000");
        Read(board, other, "Warehouse Balance 2,000,000");

        board.Reset("the game window was gone for a while");
        Assert.Equal(1_000_000L, board.Confirmed);

        // The two readings before the gap do not count toward the three.
        Settle(board, other);
        Read(board, other, "Warehouse Balance 2,000,000");
        Read(board, other, "Warehouse Balance 2,000,000");
        Assert.Equal(1_000_000L, board.Confirmed);
        Read(board, other, "Warehouse Balance 2,000,000");
        Assert.Equal(2_000_000L, board.Confirmed);
    }
}
