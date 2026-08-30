namespace LushbdoCompanion;

/// <summary>
/// One visual chat row can come back from OCR as several line fragments —
/// the item's icon splits a row between the verb and the bracket, and an
/// engine occasionally splits long rows on its own. Fragments that share a
/// vertical band are one row, read left to right. The board only ever sees
/// whole rows.
///
/// Sharing a band means *overlapping*, not sitting within some fraction of a
/// glyph height, because a fraction of a glyph height is not a fixed distance
/// across recognizers. Windows.Media.Ocr boxes a row at about half its pitch;
/// PaddleOCR's detector unclips its boxes and returns about 1.08× the pitch
/// for the same rows, so the old `0.6 × height` band went from a third of a
/// row to three quarters of one and started pulling neighbours in. Overlap
/// tells the two cases apart on its own terms: two fragments of one row cover
/// nearly the same scanlines, while boxes a full pitch apart overlap barely a
/// fifth of their height even when they are taller than the pitch.
/// </summary>
public static class OcrRows
{
    /// <summary>Share of the shorter box that must overlap for two fragments to be one row.</summary>
    private const double SameRowOverlap = 0.5;

    public readonly record struct Piece(double X, double Y, double Height, string Text);

    public static List<LineBoard.OcrLineInput> Merge(List<Piece> pieces)
    {
        pieces.Sort((a, b) => a.Y.CompareTo(b.Y));
        var rows = new List<LineBoard.OcrLineInput>();
        var band = new List<Piece>();
        for (var i = 0; i < pieces.Count;)
        {
            band.Clear();
            var anchor = pieces[i];
            while (i < pieces.Count && SameRow(anchor, pieces[i]))
                band.Add(pieces[i++]);

            band.Sort((a, b) => a.X.CompareTo(b.X));
            double top = double.MaxValue, bottom = double.MinValue;
            foreach (var p in band)
            {
                top = Math.Min(top, p.Y);
                bottom = Math.Max(bottom, p.Y + p.Height);
            }
            rows.Add(new LineBoard.OcrLineInput(string.Join(' ', band.Select(p => p.Text)), top, bottom - top));
        }
        return rows;
    }

    /// <summary>
    /// Do these two boxes cover enough of the same scanlines to be fragments
    /// of one row? Measured against the shorter of the two, so a tall box next
    /// to a short one is judged on whether the short one is inside it.
    /// </summary>
    private static bool SameRow(Piece anchor, Piece other)
    {
        var overlap = Math.Min(anchor.Y + anchor.Height, other.Y + other.Height) - Math.Max(anchor.Y, other.Y);
        if (overlap <= 0) return false;
        var shorter = Math.Max(1, Math.Min(anchor.Height, other.Height));
        return overlap >= SameRowOverlap * shorter;
    }
}
