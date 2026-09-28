using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="ScreenDoor"/> mirrors the stream-in screen door in test-fragment.glsl. The rules:
/// the threshold is a unit-range value, the fraction of pixels a fade keeps tracks the fade, a
/// full fade keeps everything and a zero fade nothing, and a pixel once kept stays kept as the
/// chunk fades further in (no flicker).
/// </summary>
public sealed class ScreenDoorTests
{
    private const int Block = 64;

    [Fact]
    public void ThresholdStaysInUnitRange()
    {
        for (var y = 0; y < Block; y++)
        for (var x = 0; x < Block; x++)
        {
            var t = ScreenDoor.Threshold(x + 0.5f, y + 0.5f);
            Assert.InRange(t, 0f, 1f);
            Assert.True(t < 1f);
        }
        // Far from the origin too, where a 1080p/4K frame's pixel centres sit.
        var far = ScreenDoor.Threshold(3839.5f, 2159.5f);
        Assert.InRange(far, 0f, 1f);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    public void KeptFractionTracksAlpha(float alpha)
    {
        Assert.InRange(KeptFraction(alpha, 0, 0), alpha - 0.03f, alpha + 0.03f);
        Assert.InRange(KeptFraction(alpha, 1000, 600), alpha - 0.03f, alpha + 0.03f);
    }

    [Fact]
    public void FullAndZeroAlphaKeepAllAndNone()
    {
        Assert.Equal(1f, KeptFraction(1f, 0, 0));
        Assert.Equal(0f, KeptFraction(0f, 0, 0));
    }

    [Fact]
    public void KeptPixelsStayKeptAsAlphaRises()
    {
        for (var y = 0; y < Block; y++)
        for (var x = 0; x < Block; x++)
        {
            var kept = false;
            for (var step = 0; step <= 20; step++)
            {
                var keeps = ScreenDoor.Keeps(step / 20f, x + 0.5f, y + 0.5f);
                Assert.False(kept && !keeps);
                kept |= keeps;
            }
            Assert.True(kept);
        }
    }

    private static float KeptFraction(float alpha, int x0, int y0)
    {
        var kept = 0;
        for (var y = 0; y < Block; y++)
        for (var x = 0; x < Block; x++)
            if (ScreenDoor.Keeps(alpha, x0 + x + 0.5f, y0 + y + 0.5f))
                kept++;
        return kept / (float)(Block * Block);
    }
}
