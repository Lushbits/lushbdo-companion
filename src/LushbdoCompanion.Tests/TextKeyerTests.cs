using Xunit;

namespace LushbdoCompanion.Tests;

/// <summary>
/// The keying rule in miniature: a glyph is a bright core with true dark
/// within reach (the outline the game draws so text reads over anything).
/// Bare backgrounds — bright or dark — have one half of that, never both.
/// </summary>
public class TextKeyerTests
{
    private static byte[] Flat(int w, int h, Func<int, int, byte> value)
    {
        var img = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Set(img, w, x, y, value(x, y));
        return img;
    }

    private static void Set(byte[] img, int w, int x, int y, byte v)
    {
        img[(y * w + x) * 4] = v;
        img[(y * w + x) * 4 + 1] = v;
        img[(y * w + x) * 4 + 2] = v;
        img[(y * w + x) * 4 + 3] = 255;
    }

    private static byte KeyedAt(byte[] dst, int w, int x, int y) => dst[(y * w + x) * 4];

    [Fact]
    public void OutlinedGlyphsKeyOnAnyBackgroundAndBareBackgroundsDoNot()
    {
        const int w = 24, h = 16;
        // Left half dark ground, right half bright sky — the wash-out case.
        var img = Flat(w, h, (x, _) => x < 12 ? (byte)30 : (byte)200);
        // An outlined "glyph" on each side: a bright core wrapped in black.
        foreach (var cx in new[] { 5, 18 })
        {
            for (var y = 7; y <= 9; y++)
                for (var x = cx - 1; x <= cx + 1; x++)
                    Set(img, w, x, y, 0);
            Set(img, w, cx, 8, 255);
        }

        var dst = new byte[img.Length];
        new TextKeyer().Key(img, w, h, dst);

        Assert.True(KeyedAt(dst, w, 5, 8) >= TextKeyer.MinCore);  // over dark ground
        Assert.True(KeyedAt(dst, w, 18, 8) >= TextKeyer.MinCore); // over bright sky
        Assert.Equal(0, KeyedAt(dst, w, 2, 2));   // bare dark ground
        Assert.Equal(0, KeyedAt(dst, w, 21, 2));  // bare bright sky — bright alone is not text
    }

    [Fact]
    public void ColoredTextCountsAsBright()
    {
        const int w = 12, h = 12;
        var img = Flat(w, h, (_, _) => 90); // mid grey world
        for (var y = 5; y <= 7; y++)
            for (var x = 5; x <= 7; x++)
                Set(img, w, x, y, 0);
        // A green item-name pixel: dim in red/blue, bright in green.
        var i = (6 * w + 6) * 4;
        img[i] = 40; img[i + 1] = 220; img[i + 2] = 40;

        var dst = new byte[img.Length];
        new TextKeyer().Key(img, w, h, dst);

        Assert.True(KeyedAt(dst, w, 6, 6) >= TextKeyer.MinCore);
    }
}
