using LushbdoCompanion;
using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The strict shape and the anchor (#22). A balance has no register behind it,
/// so a wrong figure would land silently and stay — and the tests that matter
/// most here are the ones asserting a refusal. Every refusal case below came
/// out of a real field session rather than imagination.
/// </summary>
public class BalanceParserTests
{
    [Theory]
    [InlineData("Warehouse Balance 1,234,567", 1234567L)]
    [InlineData("Warehouse Balance 1.234.567", 1234567L)]
    [InlineData("Warehouse Balance 1,234,567 ", 1234567L)]
    [InlineData("warehouse balance 1,234,567", 1234567L)]
    [InlineData("Warehouse Balance 1,000", 1000L)]
    [InlineData("  Warehouse   Balance   12,345  ", 12345L)]
    public void ReadsOneWholeGroupedNumber(string text, long expected)
    {
        var reading = BalanceParser.Parse(text);
        Assert.True(reading.Ok, reading.Why.ToString());
        Assert.Equal(expected, reading.Value);
    }

    /// <summary>
    /// The sharp edge the whole grammar exists for: a dropped digit leaves a
    /// short group, and a short group is the one thing that tells a factor-of-
    /// ten misread from a real figure. `1,00` must never come back as 100.
    /// </summary>
    [Theory]
    [InlineData("Warehouse Balance 1,00")]
    [InlineData("Warehouse Balance 1,2345")]
    [InlineData("Warehouse Balance 12,34,567")]
    [InlineData("Warehouse Balance 1,234,")]
    [InlineData("Warehouse Balance ,234")]
    public void RefusesNumbersThatAreNotGroupedInThrees(string text) =>
        Assert.Equal(BalanceParser.Refusal.NotWholeGrouped, BalanceParser.Parse(text).Why);

    /// <summary>
    /// An ungrouped run is refused however short. Straight from the first
    /// field trace (2026-08-30 15:46), where a rectangle overlapping
    /// neighbouring UI read `0 Black` and `9 EXP`, both passed as bare digits,
    /// and the log confirmed 0 silver and then 9 silver. A bare digit is not a
    /// balance — it is any number the interface happens to draw nearby.
    /// </summary>
    [Theory]
    [InlineData("Warehouse Balance 9 EXP")]
    [InlineData("Warehouse Balance 123")]
    [InlineData("Warehouse Balance 1234567")]
    public void RefusesARunWithNoGrouping(string text) =>
        Assert.Equal(BalanceParser.Refusal.NotWholeGrouped, BalanceParser.Parse(text).Why);

    /// <summary>The figure that trace read correctly five times, exactly as it came back.</summary>
    [Fact]
    public void ReadsTheFieldFigure()
    {
        var reading = BalanceParser.Parse("Warehouse Balance 23,975,827,939");
        Assert.True(reading.Ok);
        Assert.Equal(23_975_827_939L, reading.Value);
    }

    /// <summary>The server label beside the rectangle, which the shape already refused.</summary>
    [Fact]
    public void RefusesTwoNumbersFromNeighbouringUi() =>
        Assert.Equal(BalanceParser.Refusal.SeveralNumbers,
            BalanceParser.Parse("Warehouse Balance EU_Season3 9").Why);

    /// <summary>
    /// The anchor, and the case that forced it: an item tooltip drawn over the
    /// rectangle reads as one clean grouped number and would otherwise have
    /// been confirmed as the member's silver. The label is the only thing that
    /// says a number is *the balance* rather than any number the interface
    /// happened to draw there.
    /// </summary>
    [Theory]
    [InlineData("- Central Market Information Market Price: 69,000,000,000 Silver")]
    [InlineData("23,975,827 Withd")]
    [InlineData("0 Black")]
    [InlineData("9 EXP")]
    [InlineData("")]
    public void RefusesAnythingThatDoesNotSayItIsTheBalance(string text) =>
        Assert.Equal(BalanceParser.Refusal.NoAnchor, BalanceParser.Parse(text).Why);

    /// <summary>
    /// The label and the figure and nothing else. Text besides those two means
    /// something is drawn over the rectangle — the Withdraw button's hover
    /// overlay was exactly this, and it cost a figure wrong by a thousand.
    /// </summary>
    [Theory]
    [InlineData("Warehouse Balance 23,975,827 Withdraw")]
    [InlineData("Warehouse Balance Market Price: 69,000,000,000 Silver")]
    // These two were the 0-silver and 9-silver confirmations of 15:46. The
    // grouping rule caught them first; the leftover word catches them now, and
    // either way they are refused.
    [InlineData("Warehouse Balance 0 Black")]
    [InlineData("Warehouse Balance 0 B Black")]
    public void RefusesTextBesidesTheLabelAndTheFigure(string text) =>
        Assert.Equal(BalanceParser.Refusal.UnexpectedText, BalanceParser.Parse(text).Why);

    /// <summary>
    /// ...but a stray glyph or two is the recognizer, not another piece of
    /// interface — the same allowance LootParser makes for the item icon.
    /// </summary>
    [Fact]
    public void ToleratesAStrayGlyphBesideTheFigure()
    {
        var reading = BalanceParser.Parse("Warehouse Balance @ 24,191,652,314");
        Assert.True(reading.Ok, reading.Why.ToString());
        Assert.Equal(24_191_652_314L, reading.Value);
    }

    /// <summary>A comma and a period grouping the same number means one of them is a misread.</summary>
    [Fact]
    public void RefusesMixedSeparators() =>
        Assert.Equal(BalanceParser.Refusal.NotWholeGrouped, BalanceParser.Parse("Warehouse Balance 1.234,567").Why);

    /// <summary>
    /// Which of two figures is the balance is not this app's to guess — and a
    /// coin glyph read as a digit lands here too, which is why the trace
    /// carries the raw text.
    /// </summary>
    [Theory]
    [InlineData("Warehouse Balance 1,234,567 / 2,000,000")]
    [InlineData("Warehouse Balance 0 1,234,567")]
    public void RefusesMoreThanOneNumber(string text) =>
        Assert.Equal(BalanceParser.Refusal.SeveralNumbers, BalanceParser.Parse(text).Why);

    [Theory]
    [InlineData("Warehouse Balance")]
    [InlineData("Warehouse Balance ...")]
    public void RefusesACropWithNoDigits(string text) =>
        Assert.Equal(BalanceParser.Refusal.NoNumber, BalanceParser.Parse(text).Why);

    [Fact]
    public void RefusesAFigureBiggerThanAnyRealBalance()
    {
        Assert.Equal(BalanceParser.Refusal.OutOfRange, BalanceParser.Parse("Warehouse Balance 999,999,999,999,999").Why);
        Assert.True(BalanceParser.Parse("Warehouse Balance 999,999,999,999").Ok);
    }

    /// <summary>The digits are never repaired; a refusal reports what it saw, unchanged.</summary>
    [Fact]
    public void KeepsTheRunItRefusedForTheLog() =>
        Assert.Equal("1,00", BalanceParser.Parse("Warehouse Balance 1,00").Text);
}
