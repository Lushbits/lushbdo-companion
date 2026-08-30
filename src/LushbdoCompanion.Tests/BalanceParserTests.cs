using LushbdoCompanion;
using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The strict shape (#22). A balance has no register behind it, so the only
/// syntactic guard against a misread is refusing everything that is not one
/// whole grouped number — and the tests that matter most here are the ones
/// asserting a refusal.
/// </summary>
public class BalanceParserTests
{
    [Theory]
    [InlineData("1,234,567", 1234567L)]
    [InlineData("1.234.567", 1234567L)]
    [InlineData("Silver 1,234,567", 1234567L)]
    [InlineData("1,234,567 Silver", 1234567L)]
    [InlineData("1,000", 1000L)]
    [InlineData("  12,345  ", 12345L)]
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
    [InlineData("1,00")]
    [InlineData("1,2345")]
    [InlineData("12,34,567")]
    [InlineData("1,234,")]
    [InlineData(",234")]
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
    [InlineData("0 Black")]
    [InlineData("0 B Black")]
    [InlineData("9 EXP")]
    [InlineData("123")]
    [InlineData("1234567")]
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
        Assert.Equal(BalanceParser.Refusal.SeveralNumbers, BalanceParser.Parse("EU_Season3 9").Why);

    /// <summary>A comma and a period grouping the same number means one of them is a misread.</summary>
    [Fact]
    public void RefusesMixedSeparators() =>
        Assert.Equal(BalanceParser.Refusal.NotWholeGrouped, BalanceParser.Parse("1.234,567").Why);

    /// <summary>
    /// Which of two figures is the balance is not this app's to guess — and a
    /// coin glyph read as a digit lands here too, which is why the trace
    /// carries the raw text.
    /// </summary>
    [Theory]
    [InlineData("1,234,567 / 2,000,000")]
    [InlineData("0 1,234,567")]
    public void RefusesMoreThanOneNumber(string text) =>
        Assert.Equal(BalanceParser.Refusal.SeveralNumbers, BalanceParser.Parse(text).Why);

    [Theory]
    [InlineData("")]
    [InlineData("Silver")]
    [InlineData("...")]
    public void RefusesACropWithNoDigits(string text) =>
        Assert.Equal(BalanceParser.Refusal.NoNumber, BalanceParser.Parse(text).Why);

    [Fact]
    public void RefusesAFigureBiggerThanAnyRealBalance()
    {
        Assert.Equal(BalanceParser.Refusal.OutOfRange, BalanceParser.Parse("999,999,999,999,999").Why);
        Assert.True(BalanceParser.Parse("999,999,999,999").Ok);
    }

    /// <summary>The digits are never repaired; a refusal reports what it saw, unchanged.</summary>
    [Fact]
    public void KeepsTheRunItRefusedForTheLog() =>
        Assert.Equal("1,00", BalanceParser.Parse("Silver 1,00").Text);
}
