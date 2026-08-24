namespace LushbdoCompanion;

/// <summary>
/// Which part of the frame still has to be read.
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
    /// What to read, in frame rows: from <paramref name="Top"/> (inclusive) to
    /// the bottom of the frame. <paramref name="Shift"/> is how far the content
    /// moved up since the last pass — 0 when it did not, and what carried-over
    /// readings have to be moved by. <paramref name="Whole"/> means the frame
    /// could not be lined up at all and nothing may be carried.
    /// </summary>
    public readonly record struct Window(int Top, int Shift, bool Whole);

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
            return new Window(0, 0, Whole: true);
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
            return new Window(0, 0, Whole: true);
        }

        // The topmost scanline that does not line up. Everything above it is
        // last pass's content, already read, and only has to be moved.
        var firstChanged = height;
        for (var y = 0; y + bestShift < height; y++)
        {
            if (Same(_now[y], _before[y + bestShift])) continue;
            firstChanged = y;
            break;
        }

        // Reach back over what arrived last pass too, so every row is read on
        // at least two different frames before it leaves the window.
        var consensusTop = height - (2 * bestShift + rowPitch);
        var top = Math.Min(firstChanged, consensusTop) - TopMargin;
        top = Math.Clamp(top, 0, Math.Max(0, height - rowPitch));

        Keep();
        return new Window(top, bestShift, Whole: top == 0);
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
