using System.Globalization;

namespace LushbdoCompanion;

/// <summary>
/// The strict shape for a silver balance (#22). A crop around the figure will
/// almost certainly contain more than digits — a `Silver` label, a coin glyph,
/// perhaps a second figure — so this takes the one digit run out of whatever
/// the recognizer returned and refuses everything else. Nothing is repaired,
/// nothing is fuzzy-matched, no letter is read back as a digit: the same
/// non-negotiable the loot lines live by.
///
/// ## The anchor
///
/// A shape check says a number is well formed. It cannot say the number is
/// *the balance*, and every failure this feature has had in the field was
/// something else getting into the rectangle: a UI counter (`0 Black`, `9
/// EXP`), a button's hover overlay eating the last digit group, and an item
/// tooltip whose `Market Price: 69,000,000,000 Silver` parses as cleanly as
/// the real figure and is wrong by any amount you like.
///
/// So the crop has to carry its own identity, the way a loot line does.
/// <see cref="LootParser"/> refuses anything without `You have obtained` and a
/// closed bracket pair; this refuses anything that is not the label, one
/// number, and nothing else. Anything unexpected in the rectangle means
/// something is over it, and the safe reading of that is no reading.
///
/// The label is matched strictly, and the field says that is right rather than
/// merely cautious: on the passes where the number came back truncated, the
/// label came back as `Warehouse Bé` and `Warehouse Balanc(`. A degraded label
/// is evidence of a degraded read, so demanding a clean one costs nothing real
/// and screens out the frames that were already wrong.
///
/// `Warehouse Balance` is a fixed English UI string, the same class of thing
/// as the chat verb <see cref="LootParser"/> already pins — not extracted game
/// data, and English-only for the same reason the rest of v1 is.
///
/// **A shape check is not correctness for a number, and that is the sharp
/// edge here.** A loot line has the site's register behind it; a balance has
/// nothing, so a plausible wrong figure would land silently. `1,000` misread
/// as `1,00` is wrong by a factor of ten and passes any "digits and commas"
/// test — which is exactly why the grammar below is *grouping-strict*: after
/// the first separator every group must be exactly three digits, so `1,00` is
/// refused rather than believed. That one rule is the only syntactic guard
/// against a dropped digit that exists, and it is why the shape is not simply
/// `[\d,.]+`.
///
/// For the same reason a run with **no separator at all is refused outright**.
/// That started as "accept up to three digits, where the game would render no
/// separator anyway" and the first field trace killed it inside a minute
/// (2026-08-30 15:46): a rectangle catching neighbouring UI read `0 Black` and
/// `9 EXP`, the bare runs passed, three of them agreed, and the log confirmed
/// **0 silver** and then **9 silver** as balances. A bare digit is not a
/// balance — it is any number the interface happens to draw nearby. Requiring
/// the grouping costs a balance under a thousand, which in this game does not
/// exist, and buys refusal of every stray digit in the frame.
///
/// **Which separator the game actually renders is a field fact nobody here
/// has** (#22 says so, and says not to guess it). So both of the plausible
/// ones are accepted — but only one of them per number, since a figure that
/// comes back with a comma *and* a period grouped the same way has had one of
/// them misread. A grouping style this refuses is one line to add once a
/// traced session shows it, and until then the app records nothing rather
/// than recording something wrong.
/// </summary>
public static class BalanceParser
{
    public enum Refusal
    {
        None,

        /// <summary>The label was not in the crop — something is covering the panel, or the rectangle misses it.</summary>
        NoAnchor,

        /// <summary>The label and a number, but text besides — something is drawn over the rectangle.</summary>
        UnexpectedText,

        /// <summary>The crop came back with no digits at all — usually no panel behind the rectangle.</summary>
        NoNumber,

        /// <summary>Two or more numbers in the crop. Which one is the balance is not this app's to guess.</summary>
        SeveralNumbers,

        /// <summary>Digits, but not one whole number grouped in threes — a dropped digit or separator reads like this.</summary>
        NotWholeGrouped,

        /// <summary>Larger than any real balance; a read that says this misread something.</summary>
        OutOfRange,
    }

    /// <summary>
    /// A parsed balance. <paramref name="Text"/> is the digit run exactly as
    /// it was read, so a refusal can be logged with the thing that caused it.
    /// </summary>
    public readonly record struct Reading(long Value, Refusal Why, string Text)
    {
        public bool Ok => Why == Refusal.None;
    }

    /// <summary>
    /// The ceiling. Far above any real balance and far below overflow, so it
    /// only ever catches a read that ran two figures together. The site is
    /// the authority on what it will accept; nothing is sent from here yet
    /// (#24), so this is the app's own sanity bound and nothing more.
    /// </summary>
    public const long Max = 1_000_000_000_000L;

    /// <summary>The grouping marks a number may be built from — one kind per number.</summary>
    private const string Separators = ",.";

    /// <summary>The words that say this crop is a balance and not some other number.</summary>
    public const string Anchor = "Warehouse Balance";

    /// <summary>
    /// How long a leftover token may be before it counts as something else
    /// being drawn over the rectangle. The same allowance
    /// <see cref="LootParser"/> makes for the item icon: a stray glyph or two
    /// is the recognizer, a word is another piece of interface.
    /// </summary>
    private const int MaxStrayToken = 3;

    public static Reading Parse(string rawText)
    {
        // OCR pads unpredictably and the label may arrive split across
        // fragments; collapse runs of whitespace so the anchor has one
        // spelling to match.
        var text = string.Join(' ', rawText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var anchorAt = text.IndexOf(Anchor, StringComparison.OrdinalIgnoreCase);
        if (anchorAt < 0) return new Reading(0, Refusal.NoAnchor, Clip(text));
        var rest = text.Remove(anchorAt, Anchor.Length);

        string? run = null;
        var runAt = -1;
        for (var i = 0; i < rest.Length;)
        {
            if (!IsNumberish(rest[i])) { i++; continue; }
            var start = i;
            while (i < rest.Length && IsNumberish(rest[i])) i++;
            var candidate = rest[start..i];
            if (!HasDigit(candidate)) continue; // a lone "." is punctuation, not a number
            if (run is not null) return new Reading(0, Refusal.SeveralNumbers, run + " / " + candidate);
            run = candidate;
            runAt = start;
        }

        if (run is null) return new Reading(0, Refusal.NoNumber, "");

        // The label, the number, and nothing else. What is left over after
        // taking both away is whatever else is drawn over the rectangle.
        var leftover = rest.Remove(runAt, run.Length);
        foreach (var token in leftover.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (token.Length > MaxStrayToken)
                return new Reading(0, Refusal.UnexpectedText, Clip(token));

        if (!TryWholeGrouped(run, out var value)) return new Reading(0, Refusal.NotWholeGrouped, run);
        if (value > Max) return new Reading(0, Refusal.OutOfRange, run);
        return new Reading(value, Refusal.None, run);
    }

    public static string Describe(Refusal why) => why switch
    {
        Refusal.NoAnchor => $"the words \"{Anchor}\" were not in it, so there is no telling that the number is a " +
                            "balance — either the rectangle does not include the label, or something is drawn over it",
        Refusal.UnexpectedText => "there is text in it besides the label and the figure, which means something is " +
                                  "drawn over the rectangle",
        Refusal.NoNumber => "there were no digits in it",
        Refusal.SeveralNumbers => "there was more than one number in it, and which one is the balance is not this app's to guess",
        Refusal.NotWholeGrouped => "it is not one whole number grouped in threes, which is what a dropped digit or separator reads like",
        Refusal.OutOfRange => "it is larger than any real balance",
        _ => "",
    };

    /// <summary>For the log: the figure the way a member would write it.</summary>
    public static string Money(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Short enough for one log line, and never reformatted.</summary>
    private static string Clip(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static bool IsNumberish(char c) => char.IsAsciiDigit(c) || Separators.Contains(c);

    private static bool HasDigit(string s)
    {
        foreach (var c in s) if (char.IsAsciiDigit(c)) return true;
        return false;
    }

    /// <summary>
    /// One number, grouped in threes by a single separator. Every other
    /// arrangement of the same characters — a short group, a mixed separator,
    /// a bare run with no grouping at all — is a read this app will not stand
    /// behind.
    /// </summary>
    private static bool TryWholeGrouped(string run, out long value)
    {
        value = 0;
        var groups = new List<string>();
        char? separator = null;
        var start = 0;
        for (var i = 0; i <= run.Length; i++)
        {
            if (i < run.Length && char.IsAsciiDigit(run[i])) continue;
            groups.Add(run[start..i]);
            if (i == run.Length) break;
            // A number grouped by a comma *and* a period has had one of them
            // misread; there is no locale that does both.
            if (separator is { } only && run[i] != only) return false;
            separator = run[i];
            start = i + 1;
        }

        // No separator means no grouping, and an ungrouped digit run is any
        // number the interface drew near the rectangle — see the summary.
        if (groups.Count < 2) return false;
        if (groups[0].Length is < 1 or > 3) return false;
        for (var g = 1; g < groups.Count; g++)
            if (groups[g].Length != 3) return false;

        var digits = string.Concat(groups);
        if (digits.Length > 18)
        {
            value = long.MaxValue; // caller reports it as out of range
            return true;
        }
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
