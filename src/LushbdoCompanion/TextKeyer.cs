namespace LushbdoCompanion;

/// <summary>
/// The planned successor to temporal stabilization: key the text out of a
/// single raw frame instead of averaging frames over time. The game draws
/// chat text as a bright core wrapped in a dark outline precisely so it
/// reads over anything; a pixel is text exactly when it is bright *and* has
/// true dark within reach (the outline sandwich). Bright sky has no dark
/// nearby; dark ground has no bright core; a glyph always has both. Keyed,
/// every frame is one crisp scroll state — no median smear when the chat
/// scrolls, no wash-out over bright scenery — and the background flattens
/// to black so "did the text change" becomes answerable per frame.
///
/// Currently trace-only: each snapshot saves its keyed twin, so the
/// thresholds get tuned against real field frames before this replaces the
/// median in the OCR path. Buffers are allocated on first use and reused.
/// </summary>
public sealed class TextKeyer
{
    /// <summary>A text core pixel is at least this bright (max of R,G,B — colored names count).</summary>
    public const byte MinCore = 140;

    /// <summary>The outline within reach is at most this bright.</summary>
    public const byte MaxOutline = 80;

    /// <summary>How far (px) the outline may sit from a core pixel.</summary>
    public const int Reach = 2;

    private byte[] _brightness = [];
    private byte[] _rowMin = [];
    private byte[] _localMin = [];

    /// <summary>
    /// Keys BGRA <paramref name="src"/> into BGRA <paramref name="dst"/>:
    /// text pixels keep their brightness (greyscale), everything else goes
    /// black. May be called with dst == src.
    /// </summary>
    public void Key(byte[] src, int width, int height, byte[] dst)
    {
        var pixels = width * height;
        if (_brightness.Length != pixels)
        {
            _brightness = new byte[pixels];
            _rowMin = new byte[pixels];
            _localMin = new byte[pixels];
        }

        for (var i = 0; i < pixels; i++)
        {
            var b = src[i * 4];
            var g = src[i * 4 + 1];
            var r = src[i * 4 + 2];
            _brightness[i] = Math.Max(b, Math.Max(g, r));
        }

        // Separable min over a (2·Reach+1)² window: rows, then columns.
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var lo = _brightness[row + x];
                for (var d = 1; d <= Reach; d++)
                {
                    if (x - d >= 0) lo = Math.Min(lo, _brightness[row + x - d]);
                    if (x + d < width) lo = Math.Min(lo, _brightness[row + x + d]);
                }
                _rowMin[row + x] = lo;
            }
        }
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var lo = _rowMin[y * width + x];
                for (var d = 1; d <= Reach; d++)
                {
                    if (y - d >= 0) lo = Math.Min(lo, _rowMin[(y - d) * width + x]);
                    if (y + d < height) lo = Math.Min(lo, _rowMin[(y + d) * width + x]);
                }
                _localMin[y * width + x] = lo;
            }
        }

        for (var i = 0; i < pixels; i++)
        {
            var isText = _brightness[i] >= MinCore && _localMin[i] <= MaxOutline;
            var v = isText ? _brightness[i] : (byte)0;
            dst[i * 4] = v;
            dst[i * 4 + 1] = v;
            dst[i * 4 + 2] = v;
            dst[i * 4 + 3] = 255;
        }
    }
}
