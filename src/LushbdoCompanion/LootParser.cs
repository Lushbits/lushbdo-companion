using System.Text.RegularExpressions;

namespace LushbdoCompanion;

/// <summary>
/// The line grammar, enumerated from live play on #2 — not assumed from a
/// screenshot. Shapes: `You have obtained [Name]. (HH:MM)`, the `xN` quantity
/// variant with comma grouping (`x1,275`, `[Silver] x995,374`), one junk token
/// between the verb and the bracket where the item's icon lands in OCR, and
/// wrapped lines whose quantity (`x4. (18:51)`) or bare timestamp (`(19:33)`)
/// arrives as its own visual line. A name long enough wraps mid-bracket: the
/// head ends without closing (`You have obtained [Deep Tide-Dyed Standardized
/// Timber`) and the remainder closes a bracket it never opened
/// (`Square] x4. (20:25)`). The bracket pair is the anchor — it survives
/// frames where everything around it mangles.
///
/// Two rules are load-bearing. The inner name is carried raw: digits are
/// normal in names (`Gold Bar 1,000G`) and a "helpful" cleanup would turn a
/// misread into a *valid different* item, which the server could never catch.
/// And the trailing timestamp is shape only — minute-granularity chat time is
/// never read as data; capture time is the only clock (#2, bdo#581).
/// </summary>
public static class LootParser
{
    public enum Kind
    {
        /// <summary>A complete obtain line: name and count both known.</summary>
        Item,

        /// <summary>An obtain line that ends at the bracket — the quantity wrapped onto the next visual line.</summary>
        NameOnly,

        /// <summary>An obtain line that wraps mid-name: the bracket opens and the line ends before it closes.</summary>
        NameOpen,

        /// <summary>The rest of a wrapped name: `Square] x4. (20:25)`. Meaningful only under a NameOpen head.</summary>
        NameTail,

        /// <summary>An obtain line whose bracketed name is exactly "Silver" — currency, not a gatherable.</summary>
        Silver,

        /// <summary>A wrapped quantity: `x4. (18:51)` alone on its line. Meaningful only under a NameOnly head.</summary>
        QuantityTail,

        /// <summary>A wrapped timestamp: `(19:33)` alone on its line. The tail of an already-complete head.</summary>
        TimestampTail,

        /// <summary>Everything else — guild chat, NPC names, mangled frames. Skipped visibly, never fatally.</summary>
        Unrecognized
    }

    public readonly record struct Reading(Kind Kind, string Name, int Count);

    private const string Verb = "You have obtained";

    // The x in `xN` is the least reliable glyph on the line (`x23` / `*23` /
    // `x"` all observed); the digits mostly hold. Accept the misread marks,
    // never a bare number — context decides those (see QuantityTail use).
    // The timestamp is shape only, never data, so its closing paren may be
    // mangled (`}` observed on keyed reads) or missing entirely.
    private const string Stamp = @"\(\d{1,2}:\d{2}\s*[)\]}]?";

    private static readonly Regex TailAfterBracket = new(
        $@"^\s*(?:(?<one>\.)|[xX×\*]\s?(?<n>\d[\d,]*)\s*\.?)\s*({Stamp})?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuantityTailShape = new(
        $@"^[xX×\*]?\s?(?<n>\d[\d,]*)\s*(?:\.\s*({Stamp})?|{Stamp})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampTailShape = new(
        $@"^\.?\s*{Stamp}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A whole line's ending — a count and/or timestamp. A real mid-name wrap
    // fragment never carries one: the tail lives on the next visual line. A
    // fragment that ends like this is a complete line whose `]` misread
    // (`)` observed in the field), and no wrap at all.
    private static readonly Regex WholeLineTail = new(
        $@"(?:[xX×\*]\s?\d[\d,]*\s*\.?\s*({Stamp})?|{Stamp})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Reading Parse(string line)
    {
        var text = Normalize(line);
        if (text.Length == 0) return new Reading(Kind.Unrecognized, "", 0);

        if (text.StartsWith(Verb, StringComparison.OrdinalIgnoreCase))
            return ParseObtainLine(text[Verb.Length..]);

        // Not an obtain line. A wrapped tail is only ever *consumed* when the
        // line above is waiting for it — classified here, decided in context,
        // so a lone "300" of guild chat can never become a count.
        if (TimestampTailShape.IsMatch(text))
            return new Reading(Kind.TimestampTail, "", 0);
        if (QuantityTailShape.Match(text) is { Success: true } tail && TryCount(tail.Groups["n"].Value, out var tailCount))
            return new Reading(Kind.QuantityTail, "", tailCount);

        // The rest of a name that wrapped mid-bracket: closes a bracket it
        // never opened, then reads like any line's tail. The name part ships
        // raw; whose name it finishes is the board's call, made by position.
        var close = text.IndexOf(']');
        if (close >= 0 && text.IndexOf('[') < 0 &&
            TailAfterBracket.Match(text[(close + 1)..]) is { Success: true } after)
        {
            if (after.Groups["one"].Success)
                return new Reading(Kind.NameTail, text[..close].Trim(), 1);
            if (TryCount(after.Groups["n"].Value, out var nameTailCount))
                return new Reading(Kind.NameTail, text[..close].Trim(), nameTailCount);
        }

        return new Reading(Kind.Unrecognized, "", 0);
    }

    private static Reading ParseObtainLine(string rest)
    {
        var open = rest.IndexOf('[');
        if (open < 0) return new Reading(Kind.Unrecognized, "", 0);

        // The item's icon OCRs as a junk token between the verb and the
        // bracket (`O`, `e`, `A`, `4`, a bullet). Skip one short token; more
        // than that is not the icon and the line is not trusted.
        var junk = rest[..open].Trim();
        if (junk.Length > 0 && (junk.Length > 3 || junk.Contains(' ')))
            return new Reading(Kind.Unrecognized, "", 0);

        var close = rest.IndexOf(']', open + 1);
        if (close < 0)
        {
            // Wrapped mid-name: the line ends inside the bracket and the
            // rest arrives as a NameTail on the next visual line — unless
            // the fragment ends like a whole line, which means the `]`
            // misread and nothing wrapped.
            var fragment = rest[(open + 1)..].Trim();
            if (fragment.Length == 0 || WholeLineTail.IsMatch(fragment))
                return new Reading(Kind.Unrecognized, "", 0);
            return new Reading(Kind.NameOpen, fragment, 0);
        }

        var name = rest[(open + 1)..close].Trim();
        if (name.Length == 0) return new Reading(Kind.Unrecognized, "", 0);

        var after = rest[(close + 1)..];
        int count;
        if (after.Trim().Length == 0)
        {
            // Wrapped mid-line: the quantity (or the lone `. (HH:MM)` of a
            // single pickup) is on the next visual line. Count unknown here.
            if (name == "Silver") return new Reading(Kind.Silver, name, 0);
            return new Reading(Kind.NameOnly, name, 0);
        }

        var m = TailAfterBracket.Match(after);
        if (!m.Success) return new Reading(Kind.Unrecognized, "", 0);
        if (m.Groups["one"].Success) count = 1;
        else if (!TryCount(m.Groups["n"].Value, out count)) return new Reading(Kind.Unrecognized, "", 0);

        return new Reading(name == "Silver" ? Kind.Silver : Kind.Item, name, count);
    }

    private static bool TryCount(string digits, out int count)
    {
        count = 0;
        if (!long.TryParse(digits.Replace(",", ""), out var n) || n <= 0) return false;
        count = (int)Math.Min(n, int.MaxValue);
        return true;
    }

    private static string Normalize(string line)
    {
        // OCR pads unpredictably; collapse runs of whitespace so the shapes
        // above see one stable spelling. Nothing inside brackets is touched
        // beyond this — the name ships raw.
        var trimmed = line.Trim();
        return trimmed.Contains("  ") ? Regex.Replace(trimmed, @"\s+", " ") : trimmed;
    }
}
