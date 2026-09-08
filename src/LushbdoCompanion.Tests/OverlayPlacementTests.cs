using System.Drawing;
using System.Text.Json;
using LushbdoCompanion;
using Xunit;

namespace LushbdoCompanion.Tests;

public class OverlayPlacementTests
{
    // A 1920×1080 game window sitting at (100, 50) on the screen, and a
    // painted bitmap of 200×60 — the numbers are chosen so every anchor
    // resolves to a distinct, hand-checkable point.
    private static readonly Rectangle Window = new(100, 50, 1920, 1080);
    private static readonly Size Painted = new(200, 60);

    [Theory]
    [InlineData(OverlayAnchor.TopLeft, 100, 50)]
    [InlineData(OverlayAnchor.TopCentre, 960, 50)]
    [InlineData(OverlayAnchor.TopRight, 1820, 50)]
    [InlineData(OverlayAnchor.MiddleLeft, 100, 560)]
    [InlineData(OverlayAnchor.Centre, 960, 560)]
    [InlineData(OverlayAnchor.MiddleRight, 1820, 560)]
    [InlineData(OverlayAnchor.BottomLeft, 100, 1070)]
    [InlineData(OverlayAnchor.BottomCentre, 960, 1070)]
    [InlineData(OverlayAnchor.BottomRight, 1820, 1070)]
    public void Every_anchor_resolves_to_its_cell_with_no_offset(OverlayAnchor anchor, int x, int y)
    {
        var placement = new OverlayPlacement(anchor, 0, 0, 2.0);
        Assert.Equal(new Point(x, y), placement.Resolve(Window, Painted));
    }

    [Fact]
    public void Offset_moves_in_from_the_anchored_edge()
    {
        // Left and top: in is right and down.
        Assert.Equal(new Point(120, 65), new OverlayPlacement(OverlayAnchor.TopLeft, 20, 15, 2).Resolve(Window, Painted));
        // Right and bottom: in is left and up.
        Assert.Equal(new Point(1800, 1055), new OverlayPlacement(OverlayAnchor.BottomRight, 20, 15, 2).Resolve(Window, Painted));
        // A centred axis has no edge: positive is right and down there.
        Assert.Equal(new Point(980, 575), new OverlayPlacement(OverlayAnchor.Centre, 20, 15, 2).Resolve(Window, Painted));
        Assert.Equal(new Point(980, 65), new OverlayPlacement(OverlayAnchor.TopCentre, 20, 15, 2).Resolve(Window, Painted));
    }

    [Fact]
    public void A_negative_offset_pushes_the_other_way()
    {
        Assert.Equal(new Point(90, 40), new OverlayPlacement(OverlayAnchor.TopLeft, -10, -10, 2).Resolve(Window, Painted));
        Assert.Equal(new Point(1830, 1080), new OverlayPlacement(OverlayAnchor.BottomRight, -10, -10, 2).Resolve(Window, Painted));
    }

    [Fact]
    public void The_default_is_top_centre_a_few_pixels_down()
    {
        var at = OverlayPlacement.Default.Resolve(Window, Painted);
        Assert.Equal(new Point(960, 56), at);
        Assert.Equal(2.0, OverlayPlacement.Default.TextPct);
    }

    [Theory]
    [InlineData(1080, 2.0, 22)]   // 21.6 → the 22 px the overlay drew before #43
    [InlineData(1440, 2.0, 29)]   // 28.8 → 29: the size scales with the window
    [InlineData(2160, 2.0, 43)]
    [InlineData(720, 2.0, 14)]
    [InlineData(300, 1.0, 10)]    // 3 px would be unreadable: the floor holds
    [InlineData(4320, 6.0, 160)]  // and the ceiling
    public void Text_size_is_a_share_of_the_window_height_in_whole_pixels(int height, double pct, float px)
    {
        var placement = new OverlayPlacement(OverlayAnchor.TopCentre, 0, 0, pct);
        Assert.Equal(px, placement.TextPx(new Size(1920, height)));
    }

    [Fact]
    public void The_old_rectangle_becomes_top_left_plus_its_offset_at_the_size_it_drew()
    {
        // #41 drew 38 % of the rectangle's height, between 12 and 56 px. A
        // 60 px tall rectangle drew 22.8 px; over a 1080 window that is 2.1 %.
        var migrated = OverlayPlacement.FromRegion(new Rectangle(1500, 40, 300, 60), 1080);
        Assert.Equal(OverlayAnchor.TopLeft, migrated.Anchor);
        Assert.Equal(1500, migrated.OffsetX);
        Assert.Equal(40, migrated.OffsetY);
        Assert.Equal(2.1, migrated.TextPct);
        // And it resolves to the rectangle's top-left, whatever was painted.
        Assert.Equal(new Point(1600, 90), migrated.Resolve(Window, Painted));
    }

    [Fact]
    public void Migration_keeps_the_old_clamps_on_size()
    {
        // A 400 px tall rectangle drew 56 px, not 152.
        Assert.Equal(5.2, OverlayPlacement.FromRegion(new Rectangle(0, 0, 100, 400), 1080).TextPct);
        // A 10 px tall one drew 12 px, not 3.8.
        Assert.Equal(1.1, OverlayPlacement.FromRegion(new Rectangle(0, 0, 100, 10), 1080).TextPct);
        // No window height to speak of: the default size rather than a division by zero.
        Assert.Equal(OverlayPlacement.Default.TextPct, OverlayPlacement.FromRegion(new Rectangle(0, 0, 100, 60), 0).TextPct);
    }

    [Fact]
    public void Clamped_brings_a_hand_edited_file_back_inside_bounds()
    {
        var wild = new OverlayPlacement((OverlayAnchor)42, 100_000, -100_000, 99);
        var sane = wild.Clamped();
        Assert.Equal(OverlayPlacement.Default.Anchor, sane.Anchor);
        Assert.Equal(OverlayPlacement.MaxOffset, sane.OffsetX);
        Assert.Equal(-OverlayPlacement.MaxOffset, sane.OffsetY);
        Assert.Equal(OverlayPlacement.MaxTextPct, sane.TextPct);

        Assert.Equal(OverlayPlacement.MinTextPct, new OverlayPlacement(OverlayAnchor.Centre, 0, 0, 0).Clamped().TextPct);
        Assert.Equal(OverlayPlacement.Default.TextPct, new OverlayPlacement(OverlayAnchor.Centre, 0, 0, double.NaN).Clamped().TextPct);

        var fine = new OverlayPlacement(OverlayAnchor.BottomRight, 24, 24, 3.5);
        Assert.Equal(fine, fine.Clamped());
    }

    [Theory]
    [InlineData(150, 80, OverlayAnchor.TopLeft)]          // centre lands in the top-left third
    [InlineData(960, 80, OverlayAnchor.TopCentre)]
    [InlineData(1700, 80, OverlayAnchor.TopRight)]
    [InlineData(150, 560, OverlayAnchor.MiddleLeft)]
    [InlineData(960, 560, OverlayAnchor.Centre)]
    [InlineData(1700, 560, OverlayAnchor.MiddleRight)]
    [InlineData(150, 1000, OverlayAnchor.BottomLeft)]
    [InlineData(960, 1000, OverlayAnchor.BottomCentre)]
    [InlineData(1700, 1000, OverlayAnchor.BottomRight)]
    public void A_drop_hangs_from_the_cell_it_landed_in_and_resolves_back_to_the_same_spot(int x, int y, OverlayAnchor expected)
    {
        var dropped = new Point(x, y);
        var placement = OverlayPlacement.Default.At(Window, Painted, dropped);
        Assert.Equal(expected, placement.Anchor);
        Assert.Equal(dropped, placement.Resolve(Window, Painted));
        Assert.Equal(OverlayPlacement.Default.TextPct, placement.TextPct); // a drag never changes the size
    }

    [Fact]
    public void A_drop_at_a_corner_is_a_small_inset_from_that_corner()
    {
        // 24 px in from the bottom-right: exactly what a member would type.
        var at = new Point(Window.Right - Painted.Width - 24, Window.Bottom - Painted.Height - 24);
        var placement = OverlayPlacement.Default.At(Window, Painted, at);
        Assert.Equal(new OverlayPlacement(OverlayAnchor.BottomRight, 24, 24, OverlayPlacement.Default.TextPct), placement);
    }

    [Fact]
    public void A_drop_outside_the_window_still_hangs_from_the_nearest_edge()
    {
        var placement = OverlayPlacement.Default.At(Window, Painted, new Point(Window.Left - 500, Window.Top - 300));
        Assert.Equal(OverlayAnchor.TopLeft, placement.Anchor);
        Assert.Equal(-500, placement.OffsetX);
        Assert.Equal(-300, placement.OffsetY);
    }

    [Fact]
    public void Round_trips_through_json_with_the_anchor_by_name()
    {
        var placement = new OverlayPlacement(OverlayAnchor.BottomRight, 24, 32, 2.5);
        var json = JsonSerializer.Serialize(placement);
        Assert.Contains("\"Anchor\":\"BottomRight\"", json);
        Assert.Equal(placement, JsonSerializer.Deserialize<OverlayPlacement>(json));
    }
}
