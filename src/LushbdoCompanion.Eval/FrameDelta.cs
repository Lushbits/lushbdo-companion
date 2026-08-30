namespace LushbdoCompanion;

/// <summary>
/// Which part of the frame still has to be read.
///
/// **Not on the watcher's path today, and the reason is worth keeping.** The
/// arithmetic here is right and it halved the measured cost, but a partial
/// read means handing the board the rows above the window already moved — and
/// the board measures the scroll by voting on the text it is handed, so it
/// reads dy 0 however far the chat really went. Its provenance gate authorises
/// new lines only in proportion to that number, so the budget went to zero and
/// genuinely new pickups at the bottom edge were never tracked at all: twenty
/// Black Gem Fragment, twenty Fairy Powder and eight Fairy&apos;s Breath lost from
/// one eight-minute run (field log, 2026-08-25 00:06).
///
/// Making it sound means the board taking the shift as told instead of voting
/// it, which puts a pixel measurement on the path that decides how many new
/// lines may exist — where being wrong is a double count, the one outcome this
/// app may never produce. That needs its own field proof, not a tuning change.
/// Until then this class serves the eval harness, and the watcher reads the
/// whole frame.
///
/// PaddleOCR is the accurate reader and the expensive one — about 36 ms of one
/// core per row, so re-reading a seventeen-row region twice a second is not a
/// thing that can run beside a game. But most of those rows are the same rows
/// as last pass, moved up. The keyed frame makes that answerable in pixels:
/// the world behind the transparent chat is already flattened to black, so
/// what is left changes only when the *text* does.
///
/// Each scanline gets a 64-bit occupancy fingerprint — one bit per column
/// bucket, set when any text pixel falls in it. The scroll is then the shift
/// that lines this frame's fingerprints up with the last one's, and everything
/// that lines up is content already read. What must be read is what did not:
/// the new rows at the bottom, plus anything above them that genuinely
/// changed.
///
/// The window is never just the new rows, though, and that is deliberate.
/// Consensus is what keeps a misread from being sent (#2: nothing on one
/// frame's word), and consensus needs *independent* reads — the same row seen
/// again on a different frame, over a different piece of moving world. So the
/// window always reaches back far enough to cover the rows that arrived last
/// pass as well, which gives every row at least two reads before it climbs out
/// of it. Reading costs what the loot rate costs, and nothing when the chat is
/// still.
///
/// Buffers are allocated on first use and reused; a pass allocates nothing.
/// </summary>
public sealed class FrameDelta
{
    /// <summary>Column buckets per scanline. 64 over a chat's width is ~8px each — a glyph or two.</summary>
    private const int Buckets = 64;

    /// <summary>A pixel counts as text at all when the keyer left it this bright.</summary>
    private const byte Ink = 40;

    /// <summary>Buckets that may differ and still call two scanlines the same row: keyed scenery specks.</summary>
    private const int SlackBuckets = 2;

    /// <summary>Below this share of scanlines lining up, the frame is not a shifted version of the last one.</summary>
    private const double MinAgreement = 0.55;

    /// <summary>How far up the chat may have scrolled between passes, in row pitches.</summary>
    private const int MaxShiftRows = 10;

    /// <summary>Slack above the window so a row's ascenders are never clipped off the crop.</summary>
    private const int TopMargin = 6;

    /// <summary>
    /// How much of a row has to mismatch, as a share of the pitch, before the
    /// mismatch counts. Below this it is a clipped edge row or keyed scenery,
    /// not content that changed.
    /// </summary>
    private const double MinChangedRun = 0.4;

    /// <summary>
    /// What to read, in frame rows: from <paramref name="Top"/> (inclusive) to
    /// the bottom of the frame. <paramref name="Shift"/> is how far the content
    /// moved up since the last pass — 0 when it did not, and what carried-over
    /// readings have to be moved by. <paramref name="Whole"/> means the frame
    /// could not be lined up at all and nothing may be carried.
    /// </summary>
    public readonly record struct Window(int Top, int Shift, bool Whole, int FirstChanged);

    private ulong[] _now = [];
    private ulong[] _before = [];
    private int _width;
    private int _height;
    private bool _havePrevious;

    /// <summary>Forget the previous frame: the next pass reads everything and carries nothing.</summary>
    public void Reset() => _havePrevious = false;

    /// <summary>
    /// Fingerprint this keyed frame, line it up against the last one, and say
    /// what has to be read. Call once per OCR pass, in order.
    /// </summary>
    public Window Compare(byte[] keyed, int width, int height, int rowPitch)
    {
        rowPitch = Math.Clamp(rowPitch, 4, Math.Max(4, height));
        if (_width != width || _height != height || _now.Length != height)
        {
            _width = width;
            _height = height;
            _now = new ulong[height];
            _before = new ulong[height];
            _havePrevious = false;
        }

        Fingerprint(keyed, width, height, _now);

        if (!_havePrevious)
        {
            Keep();
            return new Window(0, 0, Whole: true, FirstChanged: 0);
        }

        var maxShift = Math.Min(height - 1, MaxShiftRows * rowPitch);
        var bestShift = 0;
        var bestMatches = -1;
        for (var shift = 0; shift <= maxShift; shift++)
        {
            var matches = 0;
            for (var y = 0; y + shift < height; y++)
                if (Same(_now[y], _before[y + shift])) matches++;
            // Ties break toward the smaller shift: with nothing to tell two
            // alignments apart, the one claiming less new content wins.
            if (matches > bestMatches)
            {
                bestMatches = matches;
                bestShift = shift;
            }
        }

        var comparable = height - bestShift;
        if (comparable <= 0 || bestMatches < comparable * MinAgreement)
        {
            // Not a scrolled version of the last frame — a teleport, a camera
            // turn that re-keyed everything, a cleared tab. Read it all.
            Keep();
            return new Window(0, 0, Whole: true, FirstChanged: 0);
        }

        // The topmost scanline that does not line up — but only where the
        // mismatch *persists*. A row that genuinely changed mismatches for its
        // whole glyph band, fifteen scanlines or more. A row clipped by the
        // region's top edge, showing a different sliver of itself each time
        // the chat scrolls, mismatches for two or three and means nothing; so
        // does a speck of keyed scenery. Taking the first isolated mismatch
        // let one clipped row at the top open the window to the whole frame on
        // 52 of 60 field passes (2026-08-24 22:34), which left this class
        // contributing nothing at all.
        var minRun = Math.Max(3, (int)(MinChangedRun * rowPitch));
        var firstChanged = height;
        var run = 0;
        for (var y = 0; y + bestShift < height; y++)
        {
            if (Same(_now[y], _before[y + bestShift]))
            {
                run = 0;
                continue;
            }
            if (++run < minRun) continue;
            firstChanged = y - run + 1;
            break;
        }

        // Reach back over what arrived last pass too, so every row is read on
        // at least two different frames before it leaves the window.
        var consensusTop = height - (2 * bestShift + rowPitch);
        var top = Math.Min(firstChanged, consensusTop) - TopMargin;
        top = Math.Clamp(top, 0, Math.Max(0, height - rowPitch));

        Keep();
        return new Window(top, bestShift, Whole: top == 0, FirstChanged: firstChanged);
    }

    private void Keep()
    {
        (_before, _now) = (_now, _before);
        _havePrevious = true;
    }

    private static bool Same(ulong a, ulong b) =>
        System.Numerics.BitOperations.PopCount(a ^ b) <= SlackBuckets;

    /// <summary>One bit per column bucket, set when the bucket holds any ink.</summary>
    private static void Fingerprint(byte[] keyed, int width, int height, ulong[] into)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            ulong bits = 0;
            for (var x = 0; x < width; x++)
                if (keyed[row + x * 4] >= Ink)
                    bits |= 1UL << (x * Buckets / width);
            into[y] = bits;
        }
    }
}
