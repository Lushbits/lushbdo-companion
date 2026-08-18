namespace LushbdoCompanion;

/// <summary>
/// One visual chat row can come back from OCR as several line fragments —
/// keying splits a row at the icon gap between the verb and the bracket,
/// and the engine occasionally splits long rows on its own. Fragments that
/// share a vertical band are one row, read left to right. The board only
/// ever sees whole rows.
/// </summary>
public static class OcrRows
{
    public readonly record struct Piece(double X, double Y, double Height, string Text);

    public static List<LineBoard.OcrLineInput> Merge(List<Piece> pieces)
    {
        pieces.Sort((a, b) => a.Y.CompareTo(b.Y));
        var rows = new List<LineBoard.OcrLineInput>();
        var band = new List<Piece>();
        for (var i = 0; i < pieces.Count;)
        {
            band.Clear();
            var bandY = pieces[i].Y;
            var bandH = Math.Max(pieces[i].Height, 8);
            while (i < pieces.Count && pieces[i].Y < bandY + 0.6 * bandH)
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
}
