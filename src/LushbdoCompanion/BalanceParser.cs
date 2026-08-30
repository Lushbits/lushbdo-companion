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

    public static Reading Parse(string text)
    {
        string? run = null;
        for (var i = 0; i < text.Length;)
        {
            if (!IsNumberish(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsNumberish(text[i])) i++;
            var candidate = text[start..i];
            if (!HasDigit(candidate)) continue; // a lone "." is punctuation, not a number
            if (run is not null) return new Reading(0, Refusal.SeveralNumbers, run + " / " + candidate);
            run = candidate;
        }

        if (run is null) return new Reading(0, Refusal.NoNumber, "");
        if (!TryWholeGrouped(run, out var value)) return new Reading(0, Refusal.NotWholeGrouped, run);
        if (value > Max) return new Reading(0, Refusal.OutOfRange, run);
        return new Reading(value, Refusal.None, run);
    }

    public static string Describe(Refusal why) => why switch
    {
        Refusal.NoNumber => "there were no digits in it",
        Refusal.SeveralNumbers => "there was more than one number in it, and which one is the balance is not this app's to guess",
        Refusal.NotWholeGrouped => "it is not one whole number grouped in threes, which is what a dropped digit or separator reads like",
        Refusal.OutOfRange => "it is larger than any real balance",
        _ => "",
    };

    /// <summary>For the log: the figure the way a member would write it.</summary>
    public static string Money(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

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
