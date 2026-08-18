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
}
