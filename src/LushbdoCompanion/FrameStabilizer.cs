using System.Numerics;

namespace LushbdoCompanion;

/// <summary>
/// Temporal stabilization (#2, owner decision): the chat background is
/// transparent by design, so the world animates behind the text and raw
/// frames OCR differently every tick. Chat glyphs are the one thing static
/// between scroll events — a per-pixel median over the last five frames keeps
/// them sharp and smears the moving background toward a blur, and OCR reads
/// the median image, never a raw frame. This uses the exact property that
/// defeated raw pixel change-detection.
///
/// All buffers are allocated on the first frame (and again only if the region
/// resizes); a tick in steady state is one buffer copy and one SIMD median.
/// </summary>
public sealed class FrameStabilizer
{
    public const int Depth = 5;

    private readonly byte[][] _ring = new byte[Depth][];
    private int _next;
    private int _filled;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The median image, valid after <see cref="Stabilize"/> returns true. Reused between ticks.</summary>
    public byte[] Stabilized { get; private set; } = [];

    /// <summary>True when the frame resized and everything downstream must treat the screen as unseen.</summary>
    public bool Add(RegionFrame frame)
    {
        var resized = frame.Width != Width || frame.Height != Height;
        if (resized)
        {
            Width = frame.Width;
            Height = frame.Height;
            var bytes = Width * Height * 4;
            for (var i = 0; i < Depth; i++) _ring[i] = new byte[bytes];
            Stabilized = new byte[bytes];
            _next = 0;
            _filled = 0;
        }

        frame.Pixels.AsSpan(0, Width * Height * 4).CopyTo(_ring[_next]);
        _next = (_next + 1) % Depth;
        if (_filled < Depth) _filled++;
        return resized;
    }

    /// <summary>
    /// Forget the ring without touching the buffers — for when the frames
    /// stopped (the game window went away) and what comes next must not be
    /// medianed together with what came before.
    /// </summary>
    public void Clear()
    {
        _next = 0;
        _filled = 0;
    }

    /// <summary>False while the ring is still warming up after a start or resize (~2.5 s at 2 fps).</summary>
    public bool Stabilize()
    {
        if (_filled < Depth) return false;
        Median5(_ring[0], _ring[1], _ring[2], _ring[3], _ring[4], Stabilized);
        return true;
    }

    /// <summary>
    /// Mean absolute difference over sampled bytes — the cheap "did the
    /// stabilized image change" question that replaces raw pixel equality as
    /// the OCR gate. Sampling every 16th byte reads ~217 KB per megapixel.
    /// </summary>
    public static double MeanAbsDiff(byte[] a, byte[] b, int length)
    {
        long sum = 0;
        var samples = 0;
        for (var i = 0; i < length; i += 16, samples++)
            sum += Math.Abs(a[i] - b[i]);
        return samples == 0 ? 0 : (double)sum / samples;
    }

    /// <summary>
    /// Median of five, per byte. min/max sorting network — discard the lowest
    /// of the pairwise lows and the highest of the pairwise highs, then take
    /// the median of three — vectorized to a register-width of pixels at a
    /// time. Verified against sorting in the tests, not by faith.
    /// </summary>
    public static void Median5(byte[] a, byte[] b, byte[] c, byte[] d, byte[] e, byte[] dst)
    {
        var length = dst.Length;
        var width = Vector<byte>.Count;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var va = new Vector<byte>(a, i);
            var vb = new Vector<byte>(b, i);
            var vc = new Vector<byte>(c, i);
            var vd = new Vector<byte>(d, i);
            var ve = new Vector<byte>(e, i);

            var f = Vector.Max(Vector.Min(va, vb), Vector.Min(vc, vd));
            var g = Vector.Min(Vector.Max(va, vb), Vector.Max(vc, vd));

            var lo = Vector.Min(ve, f);
            var hi = Vector.Max(ve, f);
            Vector.Max(lo, Vector.Min(hi, g)).CopyTo(dst, i);
        }
        for (; i < length; i++)
            dst[i] = Median5(a[i], b[i], c[i], d[i], e[i]);
    }

    public static byte Median5(byte a, byte b, byte c, byte d, byte e)
    {
        var f = Math.Max(Math.Min(a, b), Math.Min(c, d));
        var g = Math.Min(Math.Max(a, b), Math.Max(c, d));
        var lo = Math.Min((byte)f, e);
        var hi = Math.Max((byte)f, e);
        return Math.Max(lo, Math.Min(hi, (byte)g));
    }
}
