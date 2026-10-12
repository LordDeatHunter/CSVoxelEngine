using VoxelEngine.Effects.Water;
using Xunit;

namespace VoxelEngine.Tests.Effects.Water;

/// <summary>
/// The sun glitter on water (<see cref="WaterGlitter"/>, the twin of the term in test-fragment.glsl):
/// a narrow mirror lobe, sparkles that never pop, and no glitter from below, underwater, in shade or far off.
/// </summary>
public sealed class WaterGlitterTests
{
    private const float DaySun = 2f;
    private const float HighSun = 0.4f;

    [Fact]
    public void AFacetSparklesOnlyWhenItAlmostMirrorsTheSun()
    {
        Assert.Equal(1f, WaterGlitter.Sparkle(1f));
        Assert.Equal(0f, WaterGlitter.Sparkle(0.99f));
        Assert.Equal(0f, WaterGlitter.Sparkle(0f));
        var previous = 0f;
        for (var d = 0.99f; d <= 1f; d += 0.00005f)
        {
            var s = WaterGlitter.Sparkle(d);
            Assert.True(s >= previous, $"sparkle fell from {previous} to {s} lining up ({d})");
            previous = s;
        }
    }

    [Fact]
    public void ASparkleFadesInAndOutWithinEachRoll()
    {
        Assert.Equal(0f, WaterGlitter.Life(0f), 5);
        Assert.Equal(1f, WaterGlitter.Life(0.5f), 5);
        // Continuous across a re-roll: the end of one roll meets the start of the next at nothing
        Assert.True(WaterGlitter.Life(0.999f) < 0.01f);
        Assert.True(WaterGlitter.Life(1.001f) < 0.01f);
        for (var p = 0f; p <= 3f; p += 0.01f)
            Assert.InRange(WaterGlitter.Life(p), 0f, 1f);
    }

    [Fact]
    public void ASparkleIsAPointNotItsWholeCell()
    {
        Assert.Equal(1f, WaterGlitter.Spot(0f));
        var previous = 1f;
        for (var d = 0f; d <= 1f; d += 0.01f)
        {
            var s = WaterGlitter.Spot(d);
            Assert.True(s <= previous, $"spot rose from {previous} to {s} away from its centre ({d})");
            previous = s;
        }

        // The spot sits at least 0.3 cells in from the cell's edge, so it never reaches the edge
        Assert.Equal(0f, WaterGlitter.Spot(0.3f));
    }

    [Fact]
    public void ThereIsNoGlitterFromBelowOrWithTheEyeUnderwater()
    {
        Assert.True(WaterGlitter.Gate(false, 0f, 1f, 1f, HighSun, DaySun, 5f) > 0.99f);
        Assert.Equal(0f, WaterGlitter.Gate(true, 0f, 1f, 1f, HighSun, DaySun, 5f));
        Assert.Equal(0f, WaterGlitter.Gate(false, 1f, 1f, 1f, HighSun, DaySun, 5f));
    }

    [Fact]
    public void ThereIsNoGlitterInShadeAtNightOrFarOff()
    {
        Assert.Equal(0f, WaterGlitter.Gate(false, 0f, 0f, 1f, HighSun, DaySun, 5f));
        Assert.Equal(0f, WaterGlitter.Gate(false, 0f, 1f, 0f, HighSun, DaySun, 5f));
        Assert.Equal(0f, WaterGlitter.Gate(false, 0f, 1f, 1f, -0.2f, DaySun, 5f));
        Assert.Equal(0f, WaterGlitter.Gate(false, 0f, 1f, 1f, HighSun, 0f, 5f));
        Assert.Equal(0f, WaterGlitter.Gate(false, 0f, 1f, 1f, HighSun, DaySun, WorldGenerationConfig.WaterGlitterFadeFarBlocks));

        var previous = 1f;
        for (var d = 0f; d <= WorldGenerationConfig.WaterGlitterFadeFarBlocks * 1.5f; d += 0.5f)
        {
            var g = WaterGlitter.Gate(false, 0f, 1f, 1f, HighSun, DaySun, d);
            Assert.True(g <= previous, $"glitter gate rose from {previous} to {g} at {d} blocks");
            previous = g;
        }
    }
}
