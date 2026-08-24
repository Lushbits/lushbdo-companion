using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The rule that makes an expensive reader affordable: read the rows that
/// changed, carry the ones that only moved — and never carry a row that was
/// not read on two different frames, because consensus is what stops a misread
/// being sent.
/// </summary>
public class FrameDeltaTests
{
    private const int Width = 128;
    private const int Pitch = 20;
    private const int Rows = 10;
    private const int Height = Rows * Pitch;

    /// <summary>
    /// A keyed frame: `rows[i]` is row i's ink pattern, drawn as a band 60% of
    /// the pitch — the proportion the game's chat actually has (a ~13px glyph
    /// band on a 24px pitch), because the rule under test is about how many
    /// scanlines a changed row disturbs.
    /// </summary>
    private static byte[] Frame(IReadOnlyList<int> rows)
    {
        var img = new byte[Width * Height * 4];
        for (var r = 0; r < rows.Count && r < Rows; r++)
        {
            if (rows[r] == 0) continue;
            for (var y = r * Pitch + 3; y < r * Pitch + 15; y++)
                for (var x = 0; x < Width; x++)
                {
                    // A pattern the fingerprint can tell apart: which eighths
                    // of the row carry ink is a function of the row's id.
                    var bucket = x * 8 / Width;
                    if ((rows[r] >> bucket & 1) == 0) continue;
                    img[(y * Width + x) * 4] = 255;
                    img[(y * Width + x) * 4 + 1] = 255;
                    img[(y * Width + x) * 4 + 2] = 255;
                }
        }
        return img;
    }

    private static int[] Stream(int first) => Enumerable.Range(first, Rows).Select(i => i * 37 % 251 | 1).ToArray();

    [Fact]
    public void FirstFrameIsWholeAndCarriesNothing()
    {
        var delta = new FrameDelta();
        var window = delta.Compare(Frame(Stream(0)), Width, Height, Pitch);

        Assert.True(window.Whole);
        Assert.Equal(0, window.Top);
        Assert.Equal(0, window.Shift);
    }

    [Fact]
    public void AStillFrameReadsOnlyTheBottom()
    {
        var delta = new FrameDelta();
        var rows = Stream(0);
        delta.Compare(Frame(rows), Width, Height, Pitch);
        var window = delta.Compare(Frame(rows), Width, Height, Pitch);

        Assert.Equal(0, window.Shift);
        Assert.False(window.Whole);
        // Nothing changed, so the window is only the consensus reach-back:
        // one row's worth, not the region.
        Assert.True(window.Top >= Height - 2 * Pitch, $"top was {window.Top}");
    }

    [Fact]
    public void ScrollingByTwoRowsMeasuresTheShiftAndReadsTheNewRowsTwice()
    {
        var delta = new FrameDelta();
        delta.Compare(Frame(Stream(0)), Width, Height, Pitch);
        var window = delta.Compare(Frame(Stream(2)), Width, Height, Pitch);

        Assert.Equal(2 * Pitch, window.Shift);
        Assert.False(window.Whole);
        // The two rows that arrived, plus the two that arrived last pass, so
        // every row is read on two different frames before it leaves.
        Assert.True(window.Top <= Height - 4 * Pitch, $"top was {window.Top}");
        Assert.True(window.Top > 0, "a two-row scroll should not cost a whole-frame read");
    }

    [Fact]
    public void AnUnrelatedFrameIsReadWhole()
    {
        var delta = new FrameDelta();
        delta.Compare(Frame(Stream(0)), Width, Height, Pitch);
        var window = delta.Compare(Frame(Stream(100)), Width, Height, Pitch);

        Assert.True(window.Whole);
        Assert.Equal(0, window.Top);
    }

    [Fact]
    public void AChangeAboveTheBottomWidensTheWindowToReachIt()
    {
        var delta = new FrameDelta();
        var rows = Stream(0);
        delta.Compare(Frame(rows), Width, Height, Pitch);

        var edited = rows.ToArray();
        edited[2] = 0xAB; // a row mid-screen re-keyed: it must be read again
        var window = delta.Compare(Frame(edited), Width, Height, Pitch);

        Assert.Equal(0, window.Shift);
        Assert.True(window.Top <= 2 * Pitch, $"top was {window.Top}, expected to reach row 2");
    }

    [Fact]
    public void ResetForgetsThePreviousFrame()
    {
        var delta = new FrameDelta();
        var rows = Stream(0);
        delta.Compare(Frame(rows), Width, Height, Pitch);
        delta.Reset();
        var window = delta.Compare(Frame(rows), Width, Height, Pitch);

        Assert.True(window.Whole);
    }

    [Fact]
    public void SpecksOfKeyedSceneryDoNotCountAsAChangedRow()
    {
        var delta = new FrameDelta();
        var rows = Stream(0);
        var first = Frame(rows);
        delta.Compare(first, Width, Height, Pitch);

        var second = Frame(rows);
        // One stray bright pixel high up, the shape a bright rock leaves behind.
        second[((1 * Pitch + 2) * Width + 3) * 4] = 255;
        var window = delta.Compare(second, Width, Height, Pitch);

        Assert.True(window.Top >= Height - 2 * Pitch, $"a speck widened the window to {window.Top}");
    }

    /// <summary>Sets a few isolated scanlines' worth of ink, the way a keyed speck does.</summary>
    private static void Speck(byte[] img, int y, int x)
    {
        for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 3; dx++)
            {
                var i = ((y + dy) * Width + x + dx) * 4;
                img[i] = img[i + 1] = img[i + 2] = 255;
            }
    }

    [Fact]
    public void AClippedTopRowDoesNotOpenTheWindowToTheWholeFrame()
    {
        // The field shape (2026-08-24 22:34): the region's top edge cuts
        // through a chat row, so the first few scanlines show a different
        // sliver every time the chat scrolls and never line up. Taking the
        // first mismatch read 96% of the region on every pass.
        var delta = new FrameDelta();
        var rows = Stream(0);
        var first = Frame(rows);
        delta.Compare(first, Width, Height, Pitch);

        var second = Frame(rows);
        for (var y = 0; y < 4; y++)                    // the clipped sliver, redrawn
            for (var x = 0; x < Width; x += 3)
            {
                var i = (y * Width + x) * 4;
                second[i] = second[i + 1] = second[i + 2] = 255;
            }
        var window = delta.Compare(second, Width, Height, Pitch);

        Assert.False(window.Whole);
        Assert.True(window.Top >= Height - 2 * Pitch, $"top was {window.Top}");
    }

    [Fact]
    public void ScatteredKeyedSceneryDoesNotOpenTheWindow()
    {
        var delta = new FrameDelta();
        var rows = Stream(0);
        delta.Compare(Frame(rows), Width, Height, Pitch);

        var second = Frame(rows);
        Speck(second, 12, 40);
        Speck(second, 55, 96);
        Speck(second, 91, 12);
        var window = delta.Compare(second, Width, Height, Pitch);

        Assert.True(window.Top >= Height - 2 * Pitch, $"specks widened the window to {window.Top}");
    }

    [Fact]
    public void AWholeRowChangingStillOpensTheWindowToReachIt()
    {
        // The rule may not become "ignore changes": a row that really was
        // re-keyed mismatches for its whole glyph band and has to be re-read.
        var delta = new FrameDelta();
        var rows = Stream(0);
        delta.Compare(Frame(rows), Width, Height, Pitch);

        var edited = rows.ToArray();
        edited[3] = 0x5C;
        var window = delta.Compare(Frame(edited), Width, Height, Pitch);

        Assert.True(window.Top <= 3 * Pitch, $"top was {window.Top}, expected to reach row 3");
    }
}
