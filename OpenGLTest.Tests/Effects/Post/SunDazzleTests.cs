using System;
using VoxelEngine.Effects.Post;
using Xunit;

namespace VoxelEngine.Tests.Effects.Post;

/// <summary>
/// The sun dazzle (<see cref="SunDazzle"/>, the twin of dazzle-fragment.glsl): the bloom of looking
/// straight at the sun. Only facing the sun, only while its disc shows and never under water;
/// brightest at the sun and falling away from it, with a fan of rays that starts outside the core.
/// </summary>
public sealed class SunDazzleTests
{
    [Fact]
    public void TheDazzleGrowsSteadilyAsTheViewTurnsTowardTheSunAndIsGoneFacingAway()
    {
        Assert.Equal(0f, SunDazzle.Intensity(MathUtils.CosDeg(PostProcessConfig.SunDazzleFacingDegrees), 1f, 0f));
        Assert.Equal(0f, SunDazzle.Intensity(0f, 1f, 0f));
        Assert.Equal(0f, SunDazzle.Intensity(-1f, 1f, 0f));
        var previous = 0f;
        for (var degrees = 180f; degrees >= 0f; degrees -= 0.25f)
        {
            var d = SunDazzle.Intensity(MathUtils.CosDeg(degrees), 1f, 0f);
            Assert.True(d >= previous, $"the dazzle fell turning toward the sun ({degrees})");
            previous = d;
        }
    }

    [Fact]
    public void TheDazzleIsFullOnlyLookingStraightAtTheSunAndNeverExceedsItsCap()
    {
        var cap = PostProcessConfig.SunDazzleStrength;
        Assert.Equal(cap, SunDazzle.Intensity(1f, 1f, 0f), 5);
        Assert.Equal(cap, SunDazzle.Intensity(MathUtils.CosDeg(PostProcessConfig.SunDazzleFullDegrees * 0.5f), 1f, 0f), 5);
        Assert.True(SunDazzle.Intensity(MathUtils.CosDeg(PostProcessConfig.SunDazzleFacingDegrees * 0.5f), 1f, 0f) < cap);
        for (var alignment = -1f; alignment <= 1f; alignment += 0.05f)
        for (var visibility = 0f; visibility <= 1f; visibility += 0.1f)
        for (var wet = 0f; wet <= 1f; wet += 0.1f)
            Assert.InRange(SunDazzle.Intensity(alignment, visibility, wet), 0f, cap + 1e-5f);
    }

    [Fact]
    public void TheDazzleGoesWithTheSunsDiscAndUnderWater()
    {
        Assert.Equal(0f, SunDazzle.Intensity(1f, 0f, 0f));
        Assert.Equal(0f, SunDazzle.Intensity(1f, 1f, 1f));
        var previousDisc = 0f;
        var previousWet = SunDazzle.Intensity(1f, 1f, 0f);
        for (var t = 0f; t <= 1f; t += 0.05f)
        {
            var disc = SunDazzle.Intensity(1f, t, 0f);
            var wet = SunDazzle.Intensity(1f, 1f, t);
            Assert.True(disc >= previousDisc, "the dazzle fell as the disc faded in");
            Assert.True(wet <= previousWet, "the dazzle grew as the eye went under");
            previousDisc = disc;
            previousWet = wet;
        }
    }

    [Fact]
    public void ADiscThatFillsTheViewTopToBottomHasHalfTheScreenHeightAsItsRadius()
    {
        foreach (var fov in new[] { 50f, 70f, 110f })
        {
            Assert.Equal(0.5f, SunDazzle.DiscRadius(fov * 0.5f, fov), 4);
            Assert.True(SunDazzle.DiscRadius(2f, fov) < SunDazzle.DiscRadius(4f, fov), "a larger sun drew a smaller disc");
        }

        Assert.True(SunDazzle.DiscRadius(3f, 110f) < SunDazzle.DiscRadius(3f, 50f), "a wider view drew a larger disc");
    }

    [Fact]
    public void TheVisibilityEasesTheSameAtAnyFramerateAndTakesAStaleMeasurementWhole()
    {
        Assert.Equal(0f, SunDazzle.EaseShare(0f));
        Assert.Equal(1f, SunDazzle.EaseShare(float.PositiveInfinity));
        Assert.Equal(1f, SunDazzle.EaseShare(5f), 4);
        // Two half steps leave the same remainder as one whole step
        foreach (var seconds in new[] { 1f / 240f, 1f / 60f, 0.1f })
        {
            var half = 1f - SunDazzle.EaseShare(seconds * 0.5f);
            Assert.Equal(1f - SunDazzle.EaseShare(seconds), half * half, 5);
        }
    }

    [Fact]
    public void TheGlowIsBrightestAtTheSunAndFallsSteadilyAwayFromIt()
    {
        Assert.Equal(1f, SunDazzle.Falloff(0f), 5);
        var previous = 1f;
        for (var distance = 0f; distance <= 3f; distance += 0.005f)
        {
            var f = SunDazzle.Falloff(distance);
            Assert.True(f <= previous + 1e-6f, $"the dazzle rose again {distance} from the sun");
            Assert.True(f > 0f);
            previous = f;
        }

        // The veil reaches where the core does not: across the frame it still adds a little
        Assert.True(SunDazzle.Falloff(1f) > 100f * MathF.Exp(-1f / (PostProcessConfig.SunDazzleCoreRadius * PostProcessConfig.SunDazzleCoreRadius)));
    }

    [Fact]
    public void TheRaysCloseOnThemselvesStartOutsideTheCoreAndNeverOutshineTheSun()
    {
        for (var angle = -MathF.PI; angle <= MathF.PI; angle += 0.01f)
        {
            var rays = SunDazzle.Rays(angle);
            Assert.InRange(rays, 0f, 1f);
            Assert.Equal(rays, SunDazzle.Rays(angle + 2f * MathF.PI), 3);
            // Nothing of the fan at the sun itself, where its angle means nothing
            Assert.Equal(SunDazzle.Falloff(0f), SunDazzle.Glow(0f, angle), 5);
            for (var distance = 0f; distance <= 2f; distance += 0.02f)
            {
                var glow = SunDazzle.Glow(distance, angle);
                Assert.True(glow >= SunDazzle.Falloff(distance) - 1e-6f, "the rays took light away");
                Assert.True(glow <= 1f + 1e-5f, $"the dazzle outshone the sun's own place ({distance}, {angle})");
            }
        }
    }
}
