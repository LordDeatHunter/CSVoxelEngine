using System;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The godrays' strength (<see cref="Godrays"/>, the twin of the strength and phase the godrays pass
/// uses): only with the sun in view and above the horizon, strongest low, dimmed by cloud, and never
/// under water, where the underwater shafts take over.
/// </summary>
public sealed class GodraysTests
{
    private const float LowSun = 0.2f;

    [Fact]
    public void ThereAreNoGodraysWithTheSunAtOrBelowTheHorizon()
    {
        for (var e = -1f; e <= 0f; e += 0.01f)
            Assert.Equal(0f, Godrays.Intensity(1f, e, 0f, 0f));
        Assert.Equal(0f, Godrays.Intensity(1f, 0f, 0f, 0f));
    }

    [Fact]
    public void ThereAreNoGodraysUnderWater()
    {
        foreach (var e in new[] { 0.1f, LowSun, 0.9f })
            Assert.Equal(0f, Godrays.Intensity(1f, e, 0f, 1f));
        var previous = Godrays.Intensity(1f, LowSun, 0f, 0f);
        for (var wet = 0f; wet <= 1f; wet += 0.05f)
        {
            var g = Godrays.Intensity(1f, LowSun, 0f, wet);
            Assert.True(g <= previous, "the godrays grew as the eye went under");
            previous = g;
        }
    }

    [Fact]
    public void TheGodraysGrowSteadilyAsTheViewTurnsTowardTheSunAndAreGoneFacingAway()
    {
        Assert.Equal(0f, Godrays.Intensity(MathUtils.CosDeg(WorldGenerationConfig.GodraysFacingDegrees), LowSun, 0f, 0f));
        Assert.Equal(0f, Godrays.Intensity(-1f, LowSun, 0f, 0f));
        var previous = 0f;
        for (var alignment = -1f; alignment <= 1f; alignment += 0.005f)
        {
            var g = Godrays.Intensity(alignment, LowSun, 0f, 0f);
            Assert.True(g >= previous, $"the godrays fell turning toward the sun ({alignment})");
            previous = g;
        }
    }

    [Fact]
    public void ALowSunCastsTheStrongestShaftsAndNoonOnlyAFractionOfThem()
    {
        var peak = Godrays.Intensity(1f, WorldGenerationConfig.GodraysPeakElevation, 0f, 0f);
        var noon = Godrays.Intensity(1f, 1f, 0f, 0f);
        Assert.Equal(WorldGenerationConfig.GodraysStrength, peak, 5);
        Assert.True(noon > 0f && noon < peak, "noon should keep only a fraction of the low sun's shafts");
        for (var e = 0f; e <= 1f; e += 0.01f)
            Assert.InRange(Godrays.Intensity(1f, e, 0f, 0f), 0f, peak + 1e-6f);
    }

    [Fact]
    public void CloudOverTheSunDimsTheShaftsButNeverQuiteRemovesThem()
    {
        var previous = Godrays.Intensity(1f, LowSun, 0f, 0f);
        for (var cover = 0f; cover <= 1f; cover += 0.05f)
        {
            var g = Godrays.Intensity(1f, LowSun, cover, 0f);
            Assert.True(g <= previous, "more cloud brightened the shafts");
            previous = g;
        }

        Assert.True(previous > 0f, "a full cloud removed the shafts that its gaps would still cast");
    }

    [Fact]
    public void LongerSunlitPathsScatterMoreButNeverWithoutBound()
    {
        Assert.Equal(0f, Godrays.Scatter(0f));
        var previous = 0f;
        for (var blocks = 0f; blocks <= 2000f; blocks += 5f)
        {
            var s = Godrays.Scatter(blocks);
            Assert.True(s >= previous, $"a longer sunlit path scattered less ({blocks})");
            Assert.True(s < 1f || blocks > 1000f, "the scatter reached its bound on a finite path");
            previous = s;
        }

        // A ray into open sky scatters clearly more than one ending on a near trunk, which is the
        // contrast the composite turns into shafts
        var trunk = Godrays.Scatter(8f);
        var sky = Godrays.Scatter(WorldGenerationConfig.GodraysMaxDistance + WorldGenerationConfig.GodraysSkyBlocks);
        Assert.True(sky > 2f * trunk);
    }

    [Fact]
    public void TheShaftsAreBrightestLookingAtTheSunWithAFaintFloorElsewhere()
    {
        Assert.Equal(1f, Godrays.Phase(1f), 5);
        Assert.Equal(WorldGenerationConfig.GodraysPhaseFloor, Godrays.Phase(-1f), 5);
        var previous = Godrays.Phase(-1f);
        for (var degrees = 180f; degrees >= 0f; degrees -= 1f)
        {
            var p = Godrays.Phase(MathUtils.CosDeg(degrees));
            Assert.True(p >= previous - 1e-6f, $"the phase fell turning toward the sun ({degrees})");
            previous = p;
        }
    }
}
