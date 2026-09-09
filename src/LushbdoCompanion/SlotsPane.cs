using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LushbdoCompanion;

/// <summary>
/// The item slots' window (#52): up to three rows, each an icon and the
/// session's running count for one item the member put in a slot on the
/// site, laid out as one group the way <see cref="SlotsPlacement"/> says.
/// What to draw and when is <see cref="OverlayForm"/>'s call — this only
/// paints rows it is handed, in the figures' own face and outline, and asks
/// the <see cref="IconCache"/> for each picture as it goes: one not in hand
/// yet is a plain square that the next paint fills in, and one the site has
/// no art for stays a square — never another item's picture, never a
/// skipped row.
///
/// In a preview the rows are three samples with the slot's number in the
/// square, so the page names no item and needs nothing from the site.
/// </summary>
internal sealed class SlotsPane : LayeredPane
{
    /// <summary>One row: the count, and where its picture comes from — an icon path the cache resolves, or a number for a preview's placeholder square.</summary>
    public readonly record struct Row(string Count, string? IconPath, string? Placeholder);

    /// <summary>What the settings page previews with. Fixed, like the figures' samples.</summary>
    public static readonly Row[] Samples =
    [
        new("1,234", null, "1"),
        new("56", null, "2"),
        new("7", null, "3"),
    ];

    private readonly IconCache _icons;
    private readonly Func<float, Font> _makeFont;
    private Font _font;
    private float _px;

    public SlotsPane(IconCache icons, Func<float, Font> makeFont) : base("LushBDO Companion item slots")
    {
        _icons = icons;
        _makeFont = makeFont;
        _font = makeFont(1);
    }

    /// <summary>The font for a game window of this size; true when it changed, which is a repaint owed.</summary>
    public bool Fit(SlotsPlacement placement, Size window)
    {
        var px = placement.TextPx(window);
        if (px == _px) return false;
        var font = _makeFont(px);
        _font.Dispose();
        _font = font;
        _px = px;
        return true;
    }

    /// <summary>
    /// Paint the rows into one alpha bitmap and push it. Each row is an icon
    /// square the height of the text, a gap, and the count; the rows are
    /// arranged by the placement's direction and spacing, and the bitmap is
    /// padded for the outline. Nothing is kept between paints but the font.
    /// </summary>
    public void Render(SlotsPlacement placement, IReadOnlyList<Row> rows, Size window, bool preview)
    {
        float lineHeight;
        var cells = new SizeF[rows.Count];
        using (var measure = Graphics.FromHwnd(IntPtr.Zero))
        {
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            lineHeight = _font.GetHeight(measure);
            var side = MathF.Round(lineHeight);
            var gap = MathF.Round(lineHeight * 0.3f);
            for (var i = 0; i < rows.Count; i++)
            {
                var text = measure.MeasureString(rows[i].Count, _font, PointF.Empty, StringFormat.GenericTypographic).Width;
                cells[i] = new SizeF(side + gap + text, lineHeight);
            }
        }

        var (total, origins) = SlotsPlacement.Arrange(cells, placement.SpacingPx(window), placement.Column, placement.Flow);
        var outline = Math.Max(2f, _font.Size / 9);
        var pad = outline + 2;
        var w = Math.Max(1, (int)Math.Ceiling(total.Width + pad * 2));
        var h = Math.Max(1, (int)Math.Ceiling(total.Height + pad * 2));

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // The hair of alpha a preview needs to be grabbed anywhere on it,
            // as the figures' bitmap has.
            g.Clear(preview ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var squareSide = MathF.Round(lineHeight);
            var gap = MathF.Round(lineHeight * 0.3f);
            using var path = new GraphicsPath { FillMode = FillMode.Winding };
            using var small = new Font(_font.FontFamily, Math.Max(6f, _font.Size * 0.6f), _font.Style, GraphicsUnit.Pixel);
            for (var i = 0; i < rows.Count; i++)
            {
                var x = pad + origins[i].X;
                var y = pad + origins[i].Y;
                var square = new RectangleF(x, y, squareSide, squareSide);
                var image = rows[i].IconPath is { } iconPath ? _icons.Get(iconPath) : null;
                if (image is not null)
                    g.DrawImage(image, square);
                else
                    DrawPlaceholder(g, path, square, outline, rows[i].Placeholder, small);
                path.AddString(rows[i].Count, _font.FontFamily, (int)_font.Style, _font.Size,
                    new PointF(x + squareSide + gap, y), StringFormat.GenericTypographic);
            }
            using var pen = new Pen(Color.FromArgb(210, 0, 0, 0), outline * 2) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
            g.FillPath(Brushes.White, path);
        }
        Push(bitmap);
    }

    /// <summary>
    /// The square an icon would fill, for a row whose picture is not in hand:
    /// a dark tile with a light edge, so it reads as "an item" over any
    /// scenery, and in a preview the slot's number in it. The number rides the
    /// same path as the count so it gets the same outline.
    /// </summary>
    private static void DrawPlaceholder(Graphics g, GraphicsPath text, RectangleF square, float outline, string? label, Font small)
    {
        using var tile = RoundedRectangle(square, square.Width / 6);
        using var fill = new SolidBrush(Color.FromArgb(150, 16, 16, 16));
        using var edge = new Pen(Color.FromArgb(200, 240, 240, 240), Math.Max(1f, outline / 2));
        g.FillPath(fill, tile);
        g.DrawPath(edge, tile);
        if (label is null) return;
        var size = g.MeasureString(label, small, PointF.Empty, StringFormat.GenericTypographic);
        var at = new PointF(square.X + (square.Width - size.Width) / 2, square.Y + (square.Height - size.Height) / 2);
        text.AddString(label, small.FontFamily, (int)small.Style, small.Size, at, StringFormat.GenericTypographic);
    }

    private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
