using System;
using VoxelEngine.Effects.Surfaces;
using Xunit;

namespace VoxelEngine.Tests.Effects.Surfaces;

/// <summary>
/// The ore twinkle (<see cref="OreTwinkle"/>, the twin of the term in test-fragment.glsl): an
/// occasional, once-per-cycle flash that only sunlit, nearby ore shows.
/// </summary>
public sealed class OreTwinkleTests
{
    private const float DaySun = 2f;

    [Fact]
    public void ATwinkleFlashesOnlyBrieflyInEachCycle()
    {
        Assert.Equal(1f, OreTwinkle.Flash(0.5f));
        var lit = 0;
        const int samples = 10000;
        for (var i = 0; i < samples; i++)
        {
            var f = OreTwinkle.Flash(i / (float)samples);
            Assert.InRange(f, 0f, 1f);
            if (f > 0f) lit++;
        }

        // Occasional: the flash lights at most its window of the cycle, a small share of it
        var share = lit / (float)samples;
        Assert.True(share <= 2f * WorldGenerationConfig.OreTwinkleWidth + 1e-3f, $"lit for {share} of a cycle");
        Assert.True(share < 0.25f, $"a twinkle lit for {share} of its cycle is not occasional");
    }

    [Fact]
    public void ATwinkleFlashesOncePerCycle()
    {
        foreach (var rateHash in new[] { 0f, 0.37f, 1f })
        foreach (var phaseHash in new[] { 0f, 0.5f, 0.91f })
        {
            var rate = WorldGenerationConfig.OreTwinkleRateMin
                       + (WorldGenerationConfig.OreTwinkleRateMax - WorldGenerationConfig.OreTwinkleRateMin) * rateHash;
            var period = 1f / rate;
            // Count rising edges over ten periods: one flash per cycle
            var flashes = 0;
            var wasLit = OreTwinkle.Flash(OreTwinkle.Cycle(0f, rateHash, phaseHash)) > 0f;
            for (var t = 0f; t < 10f * period; t += period / 2000f)
            {
                var lit = OreTwinkle.Flash(OreTwinkle.Cycle(t, rateHash, phaseHash)) > 0f;
                if (lit && !wasLit) flashes++;
                wasLit = lit;
            }

            Assert.InRange(flashes, 9, 11);
        }
    }

    [Fact]
    public void NoTwinkleInShadowAtNightFacingAwayOrFarOff()
    {
        Assert.True(OreTwinkle.Gate(1f, 1f, 0.6f, DaySun, 2f) > 0.99f);
        Assert.Equal(0f, OreTwinkle.Gate(0f, 1f, 0.6f, DaySun, 2f));
        Assert.Equal(0f, OreTwinkle.Gate(1f, 0.5f, 0.6f, DaySun, 2f));
        Assert.Equal(0f, OreTwinkle.Gate(1f, 1f, 0f, DaySun, 2f));
        Assert.Equal(0f, OreTwinkle.Gate(1f, 1f, 0.6f, 0f, 2f));
        Assert.Equal(0f, OreTwinkle.Gate(1f, 1f, 0.6f, DaySun, WorldGenerationConfig.OreTwinkleFadeFarBlocks));
    }

    [Fact]
    public void TheTwinkleFadesWithDistance()
    {
        var previous = OreTwinkle.Gate(1f, 1f, 0.6f, DaySun, 0f);
        for (var d = 0f; d <= WorldGenerationConfig.OreTwinkleFadeFarBlocks * 1.5f; d += 0.25f)
        {
            var g = OreTwinkle.Gate(1f, 1f, 0.6f, DaySun, d);
            Assert.True(g <= previous, $"twinkle gate rose from {previous} to {g} at {d} blocks");
            previous = g;
        }
    }
}
