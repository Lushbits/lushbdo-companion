using Xunit;

namespace LushbdoCompanion.Tests;

public class FrameStabilizerTests
{
    [Fact]
    public void ScalarMedianMatchesSorting()
    {
        var rng = new Random(1);
        for (var i = 0; i < 100_000; i++)
        {
            var v = new byte[5];
            rng.NextBytes(v);
            var expected = v.Order().ToArray()[2];
            Assert.Equal(expected, FrameStabilizer.Median5(v[0], v[1], v[2], v[3], v[4]));
        }
    }

    [Fact]
    public void VectorMedianMatchesScalar()
    {
        var rng = new Random(2);
        // Deliberately not a multiple of any vector width, to cover the tail.
        const int length = 1013;
        var frames = new byte[5][];
        for (var i = 0; i < 5; i++)
        {
            frames[i] = new byte[length];
            rng.NextBytes(frames[i]);
        }
        var dst = new byte[length];
        FrameStabilizer.Median5(frames[0], frames[1], frames[2], frames[3], frames[4], dst);
        for (var i = 0; i < length; i++)
            Assert.Equal(
                FrameStabilizer.Median5(frames[0][i], frames[1][i], frames[2][i], frames[3][i], frames[4][i]),
                dst[i]);
    }

    [Fact]
    public void RingWarmsUpOverDepthFramesAndMediansThem()
    {
        var s = new FrameStabilizer();
        // 2×1 pixels, one byte pattern per frame; the majority value must win.
        byte[][] values = [[10], [200], [10], [200], [10]];
        for (var i = 0; i < 5; i++)
        {
            var pixels = new byte[8];
            Array.Fill(pixels, values[i][0]);
            s.Add(new RegionFrame(pixels, 2, 1));
            Assert.Equal(i == 4, s.Stabilize());
        }
        Assert.All(s.Stabilized, b => Assert.Equal(10, b));
    }

    [Fact]
    public void ResizeResetsTheRing()
    {
        var s = new FrameStabilizer();
        var small = new byte[8];
        for (var i = 0; i < 5; i++) s.Add(new RegionFrame(small, 2, 1));
        Assert.True(s.Stabilize());

        var large = new byte[16];
        Assert.True(s.Add(new RegionFrame(large, 4, 1)));  // resized → reset signalled
        Assert.False(s.Stabilize());                       // and the ring warms up again
        for (var i = 0; i < 4; i++) s.Add(new RegionFrame(large, 4, 1));
        Assert.True(s.Stabilize());
    }

    [Fact]
    public void MeanAbsDiffSeparatesSameFromChanged()
    {
        var a = new byte[64 * 16];
        var b = new byte[64 * 16];
        Assert.Equal(0, FrameStabilizer.MeanAbsDiff(a, b, a.Length));
        Array.Fill(b, (byte)80);
        Assert.True(FrameStabilizer.MeanAbsDiff(a, b, a.Length) > 3.0);
    }
}
