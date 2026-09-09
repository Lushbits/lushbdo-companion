using System.Text.Json;
using LushbdoCompanion;
using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The reply as the site sends it and this app reads it. The slots
/// (bdo#728) are the newest part of the contract and the part most likely
/// to be read against a site that has not shipped them yet, so both shapes
/// are pinned: with, and from before.
/// </summary>
public class IngestAnswerTests
{
    private static IngestClient.IngestAnswer Parse(string json) =>
        JsonSerializer.Deserialize<IngestClient.IngestAnswer>(json)!;

    [Fact]
    public void A_reply_from_before_the_slots_reads_with_none()
    {
        // What bdo#725 answers today: the figures, and no slots key at all.
        var answer = Parse("""
            {"applied":true,"reason":null,
             "session":{"id":"s1","elapsedSec":600,"liveSinceSec":600,"items":4,"valueGross":1000,"valueNet":650,
                        "silverPerHourGross":6000,"silverPerHourNet":3900,"unvaluedRows":0},
             "matched":[],"held":[],"dropped":[]}
            """);
        Assert.NotNull(answer.Session);
        Assert.Null(answer.Session!.Slots);
        Assert.Equal(1000, answer.Session.ValueGross);
    }

    [Fact]
    public void Slots_arrive_in_order_with_nulls_holding_the_empty_ones()
    {
        // Exactly what bdo#729 ships (`IngestSlot`): the array is always three
        // long, an empty place is null, and each entry also says its own
        // `slot` number — which this app does not read, since the position is
        // the meaning and an unknown key is ignored on the way in.
        var answer = Parse("""
            {"applied":true,"reason":null,
             "session":{"id":"s1","elapsedSec":600,"liveSinceSec":600,"items":4,
                        "valueGross":1000,"valueNet":650,"silverPerHourGross":6000,"silverPerHourNet":3900,"unvaluedRows":0,
                        "slots":[null,
                                 {"slot":2,"itemId":752023,"name":"Vital Crystal","qty":12,"iconPath":"new_icon/03_etc/07_productmaterial/00000752023.webp"},
                                 {"slot":3,"itemId":4001,"name":"Rough Stone","qty":0,"iconPath":null}]},
             "matched":[],"held":[],"dropped":[]}
            """);
        var slots = answer.Session!.Slots!;
        Assert.Equal(3, slots.Count);
        Assert.Null(slots[0]);
        Assert.Equal("Vital Crystal", slots[1]!.Name);
        Assert.Equal(12, slots[1]!.Qty);
        Assert.Equal("new_icon/03_etc/07_productmaterial/00000752023.webp", slots[1]!.IconPath);
        Assert.Equal(0, slots[2]!.Qty);
        Assert.Null(slots[2]!.IconPath);
    }

    [Fact]
    public void A_run_with_nothing_pinned_answers_three_empty_places()
    {
        // The site's `noSheet()` and a sheet with no slot set both send this.
        var answer = Parse("""
            {"applied":false,"reason":"empty",
             "session":{"id":"s1","elapsedSec":0,"liveSinceSec":0,"items":0,
                        "valueGross":null,"valueNet":null,"silverPerHourGross":null,"silverPerHourNet":null,"unvaluedRows":0,
                        "slots":[null,null,null]},
             "matched":[],"held":[],"dropped":[]}
            """);
        var slots = answer.Session!.Slots!;
        Assert.Equal(3, slots.Count);
        Assert.All(slots, Assert.Null);
    }

    [Fact]
    public void A_slot_without_an_icon_key_at_all_still_reads()
    {
        var answer = Parse("""
            {"applied":true,"reason":null,
             "session":{"id":"s1","elapsedSec":1,"liveSinceSec":1,"items":1,
                        "slots":[{"itemId":1,"name":"Thing","qty":3}]},
             "matched":[],"held":[],"dropped":[]}
            """);
        var slot = answer.Session!.Slots![0]!;
        Assert.Equal("Thing", slot.Name);
        Assert.Null(slot.IconPath);
    }

    [Fact]
    public void A_not_live_reply_carries_no_session_and_so_no_slots()
    {
        var answer = Parse("""{"applied":false,"reason":"no-session","session":null,"matched":[],"held":[],"dropped":[]}""");
        Assert.False(answer.Applied);
        Assert.Null(answer.Session);
    }

    // --- What the app will and will not ask the site for ---------------------

    [Theory]
    [InlineData("new_icon/03_etc/07_productmaterial/00000752023.webp", true)]
    [InlineData("a.webp", true)]
    [InlineData("new_icon/06_pc_equipitem/00_common/01_weapon/00010001.webp", true)]
    [InlineData("", false)]
    [InlineData("New_Icon/x.webp", false)]        // the site lowercases every key
    [InlineData("new_icon/x.png", false)]         // only webp is served
    [InlineData("../x.webp", false)]              // no walking anywhere
    [InlineData("new_icon//x.webp", false)]       // no empty segment
    [InlineData("new_icon/./x.webp", false)]
    [InlineData("/x.webp", false)]                // no leading slash
    [InlineData("x.webp?y=1", false)]             // no query
    [InlineData("x y.webp", false)]
    public void Only_the_sites_own_icon_keys_are_ever_asked_for(string path, bool ok)
    {
        Assert.Equal(ok, IngestClient.IsIconPath(path));
    }

    [Fact]
    public void An_absurdly_long_key_is_refused()
    {
        Assert.False(IngestClient.IsIconPath(new string('a', 300) + ".webp"));
    }
}
