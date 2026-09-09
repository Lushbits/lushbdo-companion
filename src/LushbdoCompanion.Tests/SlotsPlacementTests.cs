using System.Drawing;
using System.Text.Json;
using LushbdoCompanion;
using Xunit;

namespace LushbdoCompanion.Tests;

public class SlotsPlacementTests
{
    // The same window and bitmap the figures' tests use, so the two
    // placements can be checked against each other by eye.
    private static readonly Rectangle Window = new(100, 50, 1920, 1080);
    private static readonly Size Painted = new(200, 60);

    [Fact]
    public void The_default_hangs_from_the_middle_of_the_left_edge_stacked_down()
    {
        var d = SlotsPlacement.Default;
        Assert.Equal(OverlayAnchor.MiddleLeft, d.Anchor);
        Assert.Equal(SlotFlow.Vertical, d.Flow);
        Assert.Equal(new Point(112, 560), d.Resolve(Window, Painted));
        Assert.Equal(d, d.Clamped());
    }

    [Theory]
    [InlineData(OverlayAnchor.TopLeft, 100, 50)]
    [InlineData(OverlayAnchor.Centre, 960, 560)]
    [InlineData(OverlayAnchor.BottomRight, 1820, 1070)]
    public void Resolves_exactly_as_the_figures_placement_does(OverlayAnchor anchor, int x, int y)
    {
        var slots = new SlotsPlacement(anchor, 0, 0, 2.0, 0.5, SlotFlow.Vertical);
        var figures = new OverlayPlacement(anchor, 0, 0, 2.0);
        Assert.Equal(new Point(x, y), slots.Resolve(Window, Painted));
        Assert.Equal(figures.Resolve(Window, Painted), slots.Resolve(Window, Painted));
    }

    [Fact]
    public void Offsets_are_measured_in_from_the_anchored_edge_like_the_figures()
    {
        Assert.Equal(new Point(1800, 1055),
            new SlotsPlacement(OverlayAnchor.BottomRight, 20, 15, 2, 0.5, SlotFlow.Vertical).Resolve(Window, Painted));
    }

    [Theory]
    [InlineData(1080, 2.0, 22)]
    [InlineData(1440, 2.0, 29)]
    [InlineData(300, 1.0, 10)]
    public void Text_size_is_a_share_of_the_window_height_by_the_figures_rule(int height, double pct, float px)
    {
        var placement = SlotsPlacement.Default with { TextPct = pct };
        Assert.Equal(px, placement.TextPx(new Size(1920, height)));
    }

    [Theory]
    [InlineData(1080, 0.5, 5)]    // 5.4 → 5
    [InlineData(2160, 0.5, 11)]   // 10.8 → 11: the gap scales with the window
    [InlineData(1080, 0.0, 0)]    // touching is allowed
    [InlineData(1080, 5.0, 54)]
    public void Spacing_is_a_share_of_the_window_height_too(int height, double pct, float px)
    {
        var placement = SlotsPlacement.Default with { SpacingPct = pct };
        Assert.Equal(px, placement.SpacingPx(new Size(1920, height)));
    }

    [Fact]
    public void A_drop_hangs_from_the_cell_it_landed_in_and_keeps_size_spacing_and_direction()
    {
        var before = new SlotsPlacement(OverlayAnchor.TopLeft, 0, 0, 3.0, 1.0, SlotFlow.Horizontal);
        var dropped = new Point(1700, 1000);
        var after = before.At(Window, Painted, dropped);
        Assert.Equal(OverlayAnchor.BottomRight, after.Anchor);
        Assert.Equal(dropped, after.Resolve(Window, Painted));
        Assert.Equal(3.0, after.TextPct);
        Assert.Equal(1.0, after.SpacingPct);
        Assert.Equal(SlotFlow.Horizontal, after.Flow);
    }

    [Fact]
    public void Clamped_brings_a_hand_edited_file_back_inside_bounds()
    {
        var wild = new SlotsPlacement((OverlayAnchor)42, 100_000, -100_000, 99, -3, (SlotFlow)7);
        var sane = wild.Clamped();
        Assert.Equal(SlotsPlacement.Default.Anchor, sane.Anchor);
        Assert.Equal(OverlayPlacement.MaxOffset, sane.OffsetX);
        Assert.Equal(-OverlayPlacement.MaxOffset, sane.OffsetY);
        Assert.Equal(OverlayPlacement.MaxTextPct, sane.TextPct);
        Assert.Equal(SlotsPlacement.MinSpacingPct, sane.SpacingPct);
        Assert.Equal(SlotsPlacement.Default.Flow, sane.Flow);

        Assert.Equal(SlotsPlacement.Default.SpacingPct, (SlotsPlacement.Default with { SpacingPct = double.NaN }).Clamped().SpacingPct);
        Assert.Equal(SlotsPlacement.MaxSpacingPct, (SlotsPlacement.Default with { SpacingPct = 50 }).Clamped().SpacingPct);
    }

    [Fact]
    public void Round_trips_through_json_with_the_enums_by_name()
    {
        var placement = new SlotsPlacement(OverlayAnchor.TopRight, 24, 32, 2.5, 0.8, SlotFlow.Horizontal);
        var json = JsonSerializer.Serialize(placement);
        Assert.Contains("\"Anchor\":\"TopRight\"", json);
        Assert.Contains("\"Flow\":\"Horizontal\"", json);
        Assert.Equal(placement, JsonSerializer.Deserialize<SlotsPlacement>(json));
    }

    [Fact]
    public void A_settings_file_from_before_the_slots_reads_with_the_default_placement()
    {
        // Exactly the keys 0.7.4 wrote, and nothing about slots.
        const string json = """{"BaseUrl":"https://lushbdo.com","ShowOverlay":true,"Overlay":{"Anchor":"TopCentre","OffsetX":0,"OffsetY":6,"TextPct":2.0}}""";
        var settings = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(SlotsPlacement.Default, settings.Slots);
        Assert.Equal(OverlayAnchor.TopCentre, settings.Overlay.Anchor);
    }

    // --- Laying the cells out inside the group -----------------------------

    private static readonly SizeF[] Cells = [new(120, 30), new(80, 30), new(100, 30)];

    [Fact]
    public void Stacked_rows_follow_each_other_down_with_the_spacing_between()
    {
        var (total, at) = SlotsPlacement.Arrange(Cells, 6, column: 0, SlotFlow.Vertical);
        Assert.Equal(new SizeF(120, 30 + 6 + 30 + 6 + 30), total);
        Assert.Equal(new PointF(0, 0), at[0]);
        Assert.Equal(new PointF(0, 36), at[1]);
        Assert.Equal(new PointF(0, 72), at[2]);
    }

    [Fact]
    public void Stacked_rows_align_on_the_anchored_column()
    {
        // Right-anchored: ragged on the left, flush on the right.
        var (_, right) = SlotsPlacement.Arrange(Cells, 0, column: 2, SlotFlow.Vertical);
        Assert.Equal(0, right[0].X);
        Assert.Equal(40, right[1].X);
        Assert.Equal(20, right[2].X);
        // Centred: each row centred on the widest.
        var (_, centre) = SlotsPlacement.Arrange(Cells, 0, column: 1, SlotFlow.Vertical);
        Assert.Equal(20, centre[1].X);
        Assert.Equal(10, centre[2].X);
    }

    [Fact]
    public void Side_by_side_cells_follow_each_other_across_and_share_a_middle()
    {
        var (total, at) = SlotsPlacement.Arrange([new(120, 30), new(80, 20)], 10, column: 0, SlotFlow.Horizontal);
        Assert.Equal(new SizeF(120 + 10 + 80, 30), total);
        Assert.Equal(new PointF(0, 0), at[0]);
        Assert.Equal(new PointF(130, 5), at[1]); // the shorter cell sits on the same middle
    }

    [Fact]
    public void No_cells_is_no_bitmap()
    {
        var (total, at) = SlotsPlacement.Arrange([], 6, 0, SlotFlow.Vertical);
        Assert.Equal(SizeF.Empty, total);
        Assert.Empty(at);
    }

    [Fact]
    public void One_cell_has_no_spacing_after_it()
    {
        var (total, _) = SlotsPlacement.Arrange([new(50, 20)], 99, 0, SlotFlow.Vertical);
        Assert.Equal(new SizeF(50, 20), total);
        (total, _) = SlotsPlacement.Arrange([new(50, 20)], 99, 0, SlotFlow.Horizontal);
        Assert.Equal(new SizeF(50, 20), total);
    }
}
