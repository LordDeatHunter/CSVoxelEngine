using System;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The sun's lens glare (<see cref="SunGlare"/>, the twin of the glare term in sky-fragment.glsl):
/// how strong it is for the camera's facing, the sun's height and the cloud over it, and how it
/// spreads across the sky.
/// </summary>
public sealed class SunGlareTests
{
    private const float HighSun = 0.8f;

    [Fact]
    public void TheGlareGrowsSteadilyAsTheViewTurnsTowardTheSun()
    {
        foreach (var elevation in new[] { 0.02f, 0.1f, HighSun })
        foreach (var cover in new[] { 0f, 0.5f })
        {
            var previous = SunGlare.Intensity(-1f, elevation, cover);
            for (var alignment = -1f; alignment <= 1f; alignment += 0.001f)
            {
                var g = SunGlare.Intensity(alignment, elevation, cover);
                Assert.True(g >= previous, $"glare fell from {previous} to {g} turning toward the sun ({alignment})");
                previous = g;
            }
        }
    }

    [Fact]
    public void ThereIsNoGlareFacingAwayFromTheSun()
    {
        foreach (var alignment in new[] { -1f, -0.5f, 0f, MathUtils.CosDeg(WorldGenerationConfig.SunGlareFacingDegrees) })
            Assert.Equal(0f, SunGlare.Intensity(alignment, HighSun, 0f));
    }

    [Fact]
    public void ThereIsNoGlareWithTheSunAtOrBelowTheHorizon()
    {
        for (var elevation = -1f; elevation <= 0f; elevation += 0.01f)
            Assert.Equal(0f, SunGlare.Intensity(1f, elevation, 0f));
        Assert.Equal(0f, SunGlare.Intensity(1f, 0f, 0f));
    }

    [Fact]
    public void TheGlareNeverExceedsItsCapAndReachesItOnlyLookingAtAClearHighSun()
    {
        var cap = WorldGenerationConfig.SunGlareStrength;
        for (var alignment = -1f; alignment <= 1f; alignment += 0.05f)
        for (var elevation = -1f; elevation <= 1f; elevation += 0.05f)
        for (var cover = 0f; cover <= 1f; cover += 0.1f)
            Assert.InRange(SunGlare.Intensity(alignment, elevation, cover), 0f, cap);
        Assert.Equal(cap, SunGlare.Intensity(1f, HighSun, 0f), 5);
    }

    [Fact]
    public void CloudOverTheSunFadesTheGlareAndAFullCloudHidesIt()
    {
        var previous = SunGlare.Intensity(1f, HighSun, 0f);
        for (var cover = 0f; cover <= 1f; cover += 0.01f)
        {
            var g = SunGlare.Intensity(1f, HighSun, cover);
            Assert.True(g <= previous, $"glare rose from {previous} to {g} under more cloud ({cover})");
            previous = g;
        }

        Assert.Equal(0f, SunGlare.Intensity(1f, HighSun, 1f));
    }

    [Fact]
    public void TheGlareIsBrightestAtTheSunAndGoneAQuarterTurnAway()
    {
        Assert.Equal(1f, SunGlare.Radial(1f));
        Assert.Equal(0f, SunGlare.Radial(0f));
        Assert.Equal(0f, SunGlare.Radial(-0.5f));
        var previous = 1f;
        for (var degrees = 0f; degrees <= 90f; degrees += 0.5f)
        {
            var r = SunGlare.Radial(MathUtils.CosDeg(degrees));
            Assert.True(r <= previous + 1e-6f, $"the glare rose again {degrees} degrees from the sun");
            previous = r;
        }
    }
}
