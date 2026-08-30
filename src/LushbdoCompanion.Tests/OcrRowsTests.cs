using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// One visual row, several OCR fragments — keying splits at the icon gap.
/// The merge reunites a row left to right and never merges across rows.
/// </summary>
public class OcrRowsTests
{
    [Fact]
    public void FragmentsSharingARowMergeLeftToRight()
    {
        var rows = OcrRows.Merge(
        [
            new OcrRows.Piece(200, 100, 14, "[Wolf Blood] x16. (23:07)"),
            new OcrRows.Piece(10, 101, 13, "You have obtained"),
        ]);
        var row = Assert.Single(rows);
        Assert.Equal("You have obtained [Wolf Blood] x16. (23:07)", row.Text);
        Assert.Equal(100, row.Y);
    }

    [Fact]
    public void SeparateRowsStaySeparate()
    {
        var rows = OcrRows.Merge(
        [
            new OcrRows.Piece(10, 100, 14, "You have obtained [Rough Stone]. (18:44)"),
            new OcrRows.Piece(10, 118, 14, "You have obtained [Weeds] x3. (18:45)"),
        ]);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Text.Contains("Rough Stone"));
        Assert.Contains(rows, r => r.Text.Contains("Weeds"));
    }

    // PaddleOCR's detector unclips its boxes: for the same 24px-pitch chat it
    // returns ~26-32px boxes where the OS recognizer returned ~13. Boxes
    // taller than the row they describe overlap their neighbours, so banding
    // has to be about how much they overlap and not about how tall they are.

    [Fact]
    public void TallBoxesOnAdjacentRowsStaySeparate()
    {
        // 24px pitch, 31px boxes: they overlap by 7px, under a quarter.
        var rows = OcrRows.Merge(
        [
            new OcrRows.Piece(10, 100, 31, "You have obtained [Wolf Blood] x3. (22:13)"),
            new OcrRows.Piece(10, 124, 31, "You have obtained [Fairy Powder] x2. (22:14)"),
            new OcrRows.Piece(10, 148, 31, "You have obtained [Black Gem Fragment] x2. (22:14)"),
        ]);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Text.Contains("Wolf Blood"));
        Assert.Contains(rows, r => r.Text.Contains("Fairy Powder"));
        Assert.Contains(rows, r => r.Text.Contains("Black Gem Fragment"));
    }

    [Fact]
    public void TallFragmentsOfOneRowStillMerge()
    {
        // The icon splits the row; the two halves cover the same scanlines
        // give or take a couple of pixels, however tall the boxes are.
        var rows = OcrRows.Merge(
        [
            new OcrRows.Piece(210, 102, 28, "[Wolf Blood] x22. (22:17)"),
            new OcrRows.Piece(10, 100, 31, "System You have obtained"),
        ]);
        var row = Assert.Single(rows);
        Assert.Equal("System You have obtained [Wolf Blood] x22. (22:17)", row.Text);
    }

    [Fact]
    public void AShortFragmentInsideATallBoxIsTheSameRow()
    {
        // A punctuation-sized box the detector found on its own, sitting well
        // inside the row's tall box — judged against the shorter of the two.
        var rows = OcrRows.Merge(
        [
            new OcrRows.Piece(10, 100, 30, "You have obtained"),
            new OcrRows.Piece(190, 110, 8, "/"),
            new OcrRows.Piece(210, 103, 26, "[Wolf Blood] x22. (22:17)"),
        ]);
        var row = Assert.Single(rows);
        Assert.Equal("You have obtained / [Wolf Blood] x22. (22:17)", row.Text);
    }
}
