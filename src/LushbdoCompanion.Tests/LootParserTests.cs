using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// Every shape here was observed in live play and recorded on issue #2 — the
/// grammar is enumerated, not assumed.
/// </summary>
public class LootParserTests
{
    [Fact]
    public void PlainItemWithTimestamp()
    {
        var r = LootParser.Parse("You have obtained [Vital Crystal]. (01:45)");
        Assert.Equal(LootParser.Kind.Item, r.Kind);
        Assert.Equal("Vital Crystal", r.Name);
        Assert.Equal(1, r.Count);
    }

    [Theory]
    [InlineData("You have obtained O [Rough Stone] x2. (18:44)", "Rough Stone", 2)]
    [InlineData("You have obtained e [Concentrated Magical Black Gem] x100.", "Concentrated Magical Black Gem", 100)]
    [InlineData("You have obtained A [Weeds] x6. (18:45)", "Weeds", 6)]
    [InlineData("You have obtained • [Weeds]. (18:22)", "Weeds", 1)]
    [InlineData("You have obtained 4 [Old Tree Bark]. (18:20)", "Old Tree Bark", 1)]
    public void IconJunkTokenIsSkippedNotFoldedIntoTheName(string line, string name, int count)
    {
        var r = LootParser.Parse(line);
        Assert.Equal(LootParser.Kind.Item, r.Kind);
        Assert.Equal(name, r.Name);
        Assert.Equal(count, r.Count);
    }

    [Theory]
    [InlineData("You have obtained [Black Gem] x1,275. (19:12)", 1275)]
    [InlineData("You have obtained [Rough Stone] x23", 23)] // period wrapped to the next visual line
    [InlineData("You have obtained [Flax Thread] x3. (2:07)", 3)] // one-digit hour
    public void QuantityVariants(string line, int count)
    {
        var r = LootParser.Parse(line);
        Assert.Equal(LootParser.Kind.Item, r.Kind);
        Assert.Equal(count, r.Count);
    }

    [Fact]
    public void DigitsInsideNamesAreNeverCleaned()
    {
        // A misread that drops a zero produces a *valid different* bar name;
        // the app must ship the name raw and let the server's matcher decide.
        var r = LootParser.Parse("You have obtained [Gold Bar 1,000G]. (12:00)");
        Assert.Equal(LootParser.Kind.Item, r.Kind);
        Assert.Equal("Gold Bar 1,000G", r.Name);
        Assert.Equal(1, r.Count);
    }

    [Theory]
    [InlineData("You have obtained [Silver] x995,374. (19:00)")]
    [InlineData("You have obtained [Silver]")]
    public void SilverIsClassifiedAsCurrency(string line)
    {
        Assert.Equal(LootParser.Kind.Silver, LootParser.Parse(line).Kind);
    }

    [Fact]
    public void LongNameWrapLeavesANameOnlyHead()
    {
        var r = LootParser.Parse("You have obtained [Secret Book of the Forgotten Adventurer]");
        Assert.Equal(LootParser.Kind.NameOnly, r.Kind);
        Assert.Equal("Secret Book of the Forgotten Adventurer", r.Name);
    }

    [Fact]
    public void MidNameWrapLeavesAnOpenBracketHead()
    {
        var r = LootParser.Parse("You have obtained [Deep Tide-Dyed Standardized Timber");
        Assert.Equal(LootParser.Kind.NameOpen, r.Kind);
        Assert.Equal("Deep Tide-Dyed Standardized Timber", r.Name);
    }

    [Theory]
    [InlineData("Square] x4. (20:25)", "Square", 4)]
    [InlineData("Square]. (20:25)", "Square", 1)] // single pickup wrapped mid-name
    [InlineData("Adventurer] x12.", "Adventurer", 12)]
    public void TheRestOfAWrappedNameIsANameTail(string line, string name, int count)
    {
        var r = LootParser.Parse(line);
        Assert.Equal(LootParser.Kind.NameTail, r.Kind);
        Assert.Equal(name, r.Name);
        Assert.Equal(count, r.Count);
    }

    [Theory]
    [InlineData("Square]")]                    // no count, no dot — not enough shape to trust
    [InlineData("Square] words after (20:25)")]
    [InlineData("[Square] x4. (20:25)")]       // opens its own bracket — not a tail
    public void BracketFragmentsWithoutATailShapeAreUnrecognized(string line)
    {
        Assert.Equal(LootParser.Kind.Unrecognized, LootParser.Parse(line).Kind);
    }

    [Theory]
    [InlineData("x4. (18:51)", 4)]
    [InlineData("*23.", 23)]     // the x glyph is the least reliable on the line
    [InlineData("×2. (10:10)", 2)]
    [InlineData("4. (18:51)", 4)] // x dropped entirely
    public void WrappedQuantityTails(string line, int count)
    {
        var r = LootParser.Parse(line);
        Assert.Equal(LootParser.Kind.QuantityTail, r.Kind);
        Assert.Equal(count, r.Count);
    }

    [Theory]
    [InlineData("(19:33)")]
    [InlineData(". (19:33)")]
    public void WrappedTimestampTails(string line)
    {
        Assert.Equal(LootParser.Kind.TimestampTail, LootParser.Parse(line).Kind);
    }

    [Theory]
    [InlineData("300")]                                   // a bare number is never a count
    [InlineData("Guildmate: hello there")]
    [InlineData("You have obtained some junk [Weeds].")]  // more than one token before the bracket
    [InlineData("You have obtained [Rough Stone] x\"")]   // quantity digits unreadable
    [InlineData("You have obtained []. (10:00)")]
    [InlineData("You have obtained Rough Stone x2.")]     // the bracket pair is the anchor
    [InlineData("")]
    [InlineData("   ")]
    public void EverythingElseIsUnrecognized(string line)
    {
        Assert.Equal(LootParser.Kind.Unrecognized, LootParser.Parse(line).Kind);
    }

    [Fact]
    public void CaseAndSpacingWobbleIsTolerated()
    {
        var r = LootParser.Parse("  you have obtained  [Rough   Stone]  x2.  (18:44) ");
        Assert.Equal(LootParser.Kind.Item, r.Kind);
        Assert.Equal("Rough Stone", r.Name);
        Assert.Equal(2, r.Count);
    }
}
