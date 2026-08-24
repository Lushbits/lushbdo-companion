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

    /// <summary>A keyed frame: `rows[i]` is row i's ink pattern, drawn as a 6px band.</summary>
    private static byte[] Frame(IReadOnlyList<int> rows)
    {
        var img = new byte[Width * Height * 4];
        for (var r = 0; r < rows.Count && r < Rows; r++)
        {
            if (rows[r] == 0) continue;
            for (var y = r * Pitch + 4; y < r * Pitch + 10; y++)
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
}
