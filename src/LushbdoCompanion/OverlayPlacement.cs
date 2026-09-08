using System.Drawing;
using System.Text.Json.Serialization;

namespace LushbdoCompanion;

/// <summary>
/// The nine places the overlay can hang from: corners, edge centres and the
/// middle of the game window. Row-major, three to a row — the arithmetic in
/// <see cref="OverlayPlacement"/> relies on that order.
/// </summary>
public enum OverlayAnchor
{
    TopLeft = 0, TopCentre = 1, TopRight = 2,
    MiddleLeft = 3, Centre = 4, MiddleRight = 5,
    BottomLeft = 6, BottomCentre = 7, BottomRight = 8,
}

/// <summary>
/// Where the session overlay sits and how it draws (#43): an anchor, an
/// offset from it, a text height for each of its two lines as a share of the
/// game window's height, and which line comes first. This is the one place
/// that owns the overlay's geometry; the window resolves it against the
/// game's bounds every time it follows them.
///
/// Why not a rectangle: the picker's rectangle (#41) did not survive a
/// resolution or window-size change, its height doubled as a text size nobody
/// would guess, and nothing about it could be seen until a session was live.
/// An anchor survives the window changing shape, the sizes scale with it, and
/// every number here has a visible meaning while the settings page is open.
///
/// The offset is measured <em>in</em> from the anchored edge — the way a HUD
/// element's margin is usually given — so a positive offset on a right or
/// bottom anchor moves the text left or up. On a centred axis there is no
/// edge to come in from, and positive is right and down.
///
/// The pace has its own size and defaults smaller than the value (owner ask,
/// 2026-09-09), and the order is the member's: value then pace, or the other
/// way round. Both arrived after the first files were written, so they carry
/// defaults and an older file reads as it always did.
/// </summary>
public sealed record OverlayPlacement(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OverlayAnchor Anchor,
    int OffsetX,
    int OffsetY,
    double TextPct,
    double PaceTextPct = OverlayPlacement.DefaultPaceTextPct,
    bool PaceFirst = false)
{
    public const double DefaultPaceTextPct = 1.6;

    /// <summary>Top centre of the game window, a few pixels down: where nothing of the game's own UI sits at rest.</summary>
    public static readonly OverlayPlacement Default = new(OverlayAnchor.TopCentre, 0, 6, 2.0);

    public const double MinTextPct = 1.0;
    public const double MaxTextPct = 6.0;
    public const int MaxOffset = 8192;

    // Absolute bounds on the resolved size, so a tiny windowed client never
    // draws text too small to read and a huge one never draws a banner.
    private const float MinTextPx = 10;
    private const float MaxTextPx = 160;

    /// <summary>The value line's text height in pixels for a game window of this size, whole pixels so a font is remade only when it matters.</summary>
    public float TextPx(Size window) => Px(window, TextPct);

    /// <summary>The pace line's, likewise.</summary>
    public float PaceTextPx(Size window) => Px(window, PaceTextPct);

    private static float Px(Size window, double pct) =>
        MathF.Round(Math.Clamp((float)(window.Height * pct / 100), MinTextPx, MaxTextPx));

    /// <summary>Where a painted bitmap of this size goes, in screen pixels, over a game window with these bounds.</summary>
    public Point Resolve(Rectangle window, Size painted)
    {
        var x = Column switch
        {
            0 => window.Left + OffsetX,
            1 => window.Left + (window.Width - painted.Width) / 2 + OffsetX,
            _ => window.Right - painted.Width - OffsetX,
        };
        var y = Row switch
        {
            0 => window.Top + OffsetY,
            1 => window.Top + (window.Height - painted.Height) / 2 + OffsetY,
            _ => window.Bottom - painted.Height - OffsetY,
        };
        return new Point(x, y);
    }

    /// <summary>0 left, 1 centre, 2 right — which is also how the two lines align with each other.</summary>
    public int Column => (int)Anchor % 3;

    /// <summary>0 top, 1 middle, 2 bottom.</summary>
    public int Row => (int)Anchor / 3;

    /// <summary>
    /// Where a drag left the figures, said as a placement: the anchor is the
    /// cell of the window's thirds the painted bitmap's centre landed in — so
    /// figures dropped near a corner hang from that corner and survive the
    /// window changing shape — and the offset is whatever puts the bitmap
    /// exactly there under that anchor. <see cref="Resolve"/> of the result
    /// gives the point back; the sizes and the order are untouched.
    /// </summary>
    public OverlayPlacement At(Rectangle window, Size painted, Point at)
    {
        var column = Third(at.X + painted.Width / 2 - window.Left, window.Width);
        var row = Third(at.Y + painted.Height / 2 - window.Top, window.Height);
        var x = column switch
        {
            0 => at.X - window.Left,
            1 => at.X - (window.Left + (window.Width - painted.Width) / 2),
            _ => window.Right - painted.Width - at.X,
        };
        var y = row switch
        {
            0 => at.Y - window.Top,
            1 => at.Y - (window.Top + (window.Height - painted.Height) / 2),
            _ => window.Bottom - painted.Height - at.Y,
        };
        return (this with { Anchor = (OverlayAnchor)(row * 3 + column), OffsetX = x, OffsetY = y }).Clamped();
    }

    private static int Third(int position, int extent) =>
        extent <= 0 ? 1 : Math.Clamp(position * 3 / extent, 0, 2);

    /// <summary>
    /// The same placement with every number inside its bounds. A hand-edited
    /// settings file is the only way out of them, and the answer to that is
    /// the nearest sane value rather than a window drawn off-screen.
    /// </summary>
    public OverlayPlacement Clamped() => this with
    {
        Anchor = Enum.IsDefined(Anchor) ? Anchor : Default.Anchor,
        OffsetX = Math.Clamp(OffsetX, -MaxOffset, MaxOffset),
        OffsetY = Math.Clamp(OffsetY, -MaxOffset, MaxOffset),
        TextPct = double.IsFinite(TextPct) ? Math.Clamp(TextPct, MinTextPct, MaxTextPct) : Default.TextPct,
        PaceTextPct = double.IsFinite(PaceTextPct) ? Math.Clamp(PaceTextPct, MinTextPct, MaxTextPct) : DefaultPaceTextPct,
    };

    /// <summary>
    /// What #41's rectangle meant, said in these terms, so a member who already
    /// placed the overlay sees no change: its top-left is the spot, so the
    /// anchor is top-left with that offset, and the text size it drew — 38 %
    /// of the rectangle's height, held between 12 and 56 px — becomes a share
    /// of the window height it was picked over.
    /// </summary>
    public static OverlayPlacement FromRegion(Rectangle region, int windowHeight)
    {
        var px = Math.Clamp(region.Height * 0.38, 12, 56);
        var pct = windowHeight > 0 ? Math.Round(px / windowHeight * 100, 1) : Default.TextPct;
        return new OverlayPlacement(OverlayAnchor.TopLeft, region.X, region.Y, pct).Clamped();
    }
}
