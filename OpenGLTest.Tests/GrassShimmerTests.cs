using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The grass wind shimmer (<see cref="GrassShimmer"/>, the twin of the term in test-fragment.glsl):
/// banded, carried downwind by the gusts' offset, faded with distance and only on grass tops.
/// </summary>
public sealed class GrassShimmerTests
{
    private static readonly Vector2 Wind = Vector2.Normalize(new Vector2(0.8f, 0.6f));

    [Fact]
    public void TheBandStaysBetweenNothingAndFull()
    {
        for (var x = -40f; x <= 40f; x += 0.37f)
        for (var z = -40f; z <= 40f; z += 1.9f)
            Assert.InRange(GrassShimmer.Band(new Vector2(x, z), Wind, new Vector2(3f, -2f), 12.3), 0f, 1f);
    }

    [Fact]
    public void TheBandsTravelWithTheGusts()
    {
        // Wherever the gusts have rolled to, the pattern they carry is the same pattern moved by
        // exactly that far: the bands roll downwind with the canopy's gusts.
        var t = 41.7;
        var offset = new Vector2(1.5f, -0.5f);
        foreach (var shift in new[] { new Vector2(3f, 0f), Wind * 5f, new Vector2(-2.5f, 7f) })
        for (var x = -10f; x <= 10f; x += 0.73f)
        for (var z = -10f; z <= 10f; z += 1.31f)
        {
            var p = new Vector2(x, z);
            Assert.Equal(GrassShimmer.Band(p, Wind, offset, t), GrassShimmer.Band(p + shift, Wind, offset + shift, t), 3);
        }
    }

    [Fact]
    public void TheShimmerIsBandedNotAUniformWash()
    {
        // Walking along the wind crosses bright crests and bare troughs within a few wavelengths.
        float lowest = 1f, highest = 0f;
        for (var s = 0f; s <= 3f * WorldGenerationConfig.GrassShimmerWavelengthBlocks; s += 0.1f)
        {
            var b = GrassShimmer.Band(Wind * s, Wind, Vector2.Zero, 0.0);
            lowest = MathF.Min(lowest, b);
            highest = MathF.Max(highest, b);
        }

        Assert.Equal(0f, lowest);
        Assert.True(highest > 0.25f, $"no band brighter than {highest} along three wavelengths");
    }

    [Fact]
    public void TheShimmerFadesOutWithDistance()
    {
        Assert.Equal(1f, GrassShimmer.Fade(0f));
        Assert.Equal(0f, GrassShimmer.Fade(WorldGenerationConfig.GrassShimmerFadeFarBlocks));
        var previous = 1f;
        for (var d = 0f; d <= WorldGenerationConfig.GrassShimmerFadeFarBlocks * 1.5f; d += 0.5f)
        {
            var f = GrassShimmer.Fade(d);
            Assert.True(f <= previous, $"fade rose from {previous} to {f} at {d} blocks");
            previous = f;
        }

        var p = new Vector2(2f, 3f);
        Assert.Equal(0f, GrassShimmer.Brightening(p, Wind, Vector2.Zero, 5.0, WorldGenerationConfig.GrassShimmerFadeFarBlocks + 1f));
        Assert.InRange(GrassShimmer.Brightening(p, Wind, Vector2.Zero, 5.0, 0f), 0f, WorldGenerationConfig.GrassShimmerStrength);
    }

    [Fact]
    public void OnlyGrassTopsShimmer()
    {
        Assert.True(GrassShimmer.Applies(biomeTinted: true, upFace: true, swaying: false, lodFoliage: false));
        Assert.False(GrassShimmer.Applies(biomeTinted: false, upFace: true, swaying: false, lodFoliage: false));
        Assert.False(GrassShimmer.Applies(biomeTinted: true, upFace: false, swaying: false, lodFoliage: false));
        Assert.False(GrassShimmer.Applies(biomeTinted: true, upFace: true, swaying: true, lodFoliage: false));
        Assert.False(GrassShimmer.Applies(biomeTinted: true, upFace: true, swaying: false, lodFoliage: true));
    }
}
