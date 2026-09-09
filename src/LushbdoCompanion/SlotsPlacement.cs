using System.Drawing;
using System.Text.Json.Serialization;

namespace LushbdoCompanion;

/// <summary>Which way the three slots follow each other: down the screen, or along it.</summary>
public enum SlotFlow { Vertical, Horizontal }

/// <summary>
/// Where the item slots sit and how they draw (#52): one anchor and offset
/// for the group of three, a text height as a share of the game window's
/// height — the icon is a square of the same height — a gap between the
/// slots on the same scale, and which way they run. Owner ruling
/// (2026-09-10): one anchor for the group rather than one per slot, plus a
/// spacing control and a direction, which are the two things the figures'
/// placement has no need of.
///
/// The anchor arithmetic is <see cref="OverlayPlacement"/>'s, called rather
/// than copied, so a slot group and the figures agree about what an offset
/// means. What is this record's own is the layout of the cells inside the
/// group (<see cref="Arrange"/>), which is pure arithmetic and tested as such.
///
/// The spacing is a share of the window's height like the text is, so a
/// resolution change scales the gaps with the glyphs and the group keeps its
/// proportions.
/// </summary>
public sealed record SlotsPlacement(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OverlayAnchor Anchor,
    int OffsetX,
    int OffsetY,
    double TextPct,
    double SpacingPct,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] SlotFlow Flow)
{
    /// <summary>
    /// Middle of the left edge, a little in, stacked down: the quest tracker
    /// sits above it and the skill bar well below, so at rest nothing of the
    /// game's own UI is under it, and a column reads as a list of things.
    /// </summary>
    public static readonly SlotsPlacement Default = new(OverlayAnchor.MiddleLeft, 12, 0, 2.0, 0.5, SlotFlow.Vertical);

    public const double MinSpacingPct = 0.0;
    public const double MaxSpacingPct = 5.0;

    /// <summary>The slot text's height in pixels for a game window of this size — also the icon's side.</summary>
    public float TextPx(Size window) => OverlayPlacement.Px(window, TextPct);

    /// <summary>The gap between two slots in pixels for a game window of this size, whole pixels.</summary>
    public float SpacingPx(Size window) =>
        MathF.Round(Math.Clamp((float)(window.Height * SpacingPct / 100), 0, 400));

    /// <summary>Where a painted bitmap of this size goes, in screen pixels, over a game window with these bounds.</summary>
    public Point Resolve(Rectangle window, Size painted) =>
        OverlayPlacement.Resolve(Anchor, OffsetX, OffsetY, window, painted);

    /// <summary>0 left, 1 centre, 2 right — which is also how a stacked group's rows align with each other.</summary>
    public int Column => OverlayPlacement.ColumnOf(Anchor);

    /// <summary>0 top, 1 middle, 2 bottom.</summary>
    public int Row => OverlayPlacement.RowOf(Anchor);

    /// <summary>Where a drag left the group, said as a placement — the figures' rule, see <see cref="OverlayPlacement.At"/>. Size, spacing and direction are untouched.</summary>
    public SlotsPlacement At(Rectangle window, Size painted, Point at)
    {
        var (anchor, x, y) = OverlayPlacement.Snap(window, painted, at);
        return (this with { Anchor = anchor, OffsetX = x, OffsetY = y }).Clamped();
    }

    /// <summary>The same placement with every number inside its bounds; a hand-edited file is the only way out of them.</summary>
    public SlotsPlacement Clamped() => this with
    {
        Anchor = Enum.IsDefined(Anchor) ? Anchor : Default.Anchor,
        OffsetX = Math.Clamp(OffsetX, -OverlayPlacement.MaxOffset, OverlayPlacement.MaxOffset),
        OffsetY = Math.Clamp(OffsetY, -OverlayPlacement.MaxOffset, OverlayPlacement.MaxOffset),
        TextPct = double.IsFinite(TextPct)
            ? Math.Clamp(TextPct, OverlayPlacement.MinTextPct, OverlayPlacement.MaxTextPct)
            : Default.TextPct,
        SpacingPct = double.IsFinite(SpacingPct) ? Math.Clamp(SpacingPct, MinSpacingPct, MaxSpacingPct) : Default.SpacingPct,
        Flow = Enum.IsDefined(Flow) ? Flow : Default.Flow,
    };

    /// <summary>
    /// Where each cell of the group goes inside the painted bitmap, and how
    /// big that bitmap is, before padding. Stacked, the rows line up on the
    /// anchored column the way the figures' two lines do — the edge they hang
    /// from, or each other's centre; side by side, the cells share a baseline
    /// through their middles. Empty slots are not among the cells: the caller
    /// leaves them out, so a cleared slot closes up rather than leaving a hole
    /// in the middle of a HUD.
    /// </summary>
    public static (SizeF Total, PointF[] Origins) Arrange(IReadOnlyList<SizeF> cells, float spacing, int column, SlotFlow flow)
    {
        var origins = new PointF[cells.Count];
        if (cells.Count == 0) return (SizeF.Empty, origins);

        if (flow == SlotFlow.Horizontal)
        {
            float x = 0, height = 0;
            foreach (var cell in cells) height = Math.Max(height, cell.Height);
            for (var i = 0; i < cells.Count; i++)
            {
                origins[i] = new PointF(x, (height - cells[i].Height) / 2);
                x += cells[i].Width + (i < cells.Count - 1 ? spacing : 0);
            }
            return (new SizeF(x, height), origins);
        }

        float y = 0, width = 0;
        foreach (var cell in cells) width = Math.Max(width, cell.Width);
        for (var i = 0; i < cells.Count; i++)
        {
            var x = column switch
            {
                0 => 0,
                1 => (width - cells[i].Width) / 2,
                _ => width - cells[i].Width,
            };
            origins[i] = new PointF(x, y);
            y += cells[i].Height + (i < cells.Count - 1 ? spacing : 0);
        }
        return (new SizeF(width, y), origins);
    }
}
