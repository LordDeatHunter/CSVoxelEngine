using System;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The low-health edge pulse: nothing at or above the threshold, nothing while dead or in
/// creative, a real pulse below it, and a pulse whose peak never weakens as health falls.
/// </summary>
public sealed class LowHealthVignetteTests
{
    private const float Threshold = WorldGenerationConfig.LowHealthThreshold;
    private const float Period = WorldGenerationConfig.LowHealthPulseSeconds;
    private const int SamplesPerPeriod = 400;

    [Theory]
    [InlineData(1f)]
    [InlineData(0.75f)]
    [InlineData(Threshold)]
    public void NothingAtOrAboveTheThreshold(float health01)
    {
        for (var i = 0; i < SamplesPerPeriod * 2; i++)
            Assert.Equal(0f, LowHealthVignette.Intensity(health01, i * Period / SamplesPerPeriod, false, false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NothingWhileDeadOrInCreative(bool dead, bool creative)
    {
        foreach (var health01 in new[] { 0f, Threshold * 0.1f, Threshold * 0.5f, Threshold * 0.99f })
            for (var i = 0; i < SamplesPerPeriod; i++)
                Assert.Equal(0f, LowHealthVignette.Intensity(health01, i * Period / SamplesPerPeriod, dead, creative));
    }

    [Fact]
    public void BelowTheThresholdTheEdgePulses()
    {
        var (low, high) = Range(Threshold * 0.5f);
        Assert.True(low > 0f, "the edge should never vanish between beats");
        Assert.True(high > low, "the edge should beat, not hold still");
        Assert.InRange(high, 0f, 1f);
    }

    [Fact]
    public void ThePulsePeakNeverWeakensAsHealthFalls()
    {
        var previous = 0f;
        for (var step = 1; step <= 30; step++)
        {
            var health01 = Threshold * (1f - step / 30f);
            var (_, peak) = Range(health01);
            Assert.True(peak > 0f);
            Assert.True(peak >= previous, $"peak weakened at health {health01}");
            previous = peak;
        }
    }

    // Lowest and highest intensity over one pulse period, sampled on the same clock at every health.
    private static (float Low, float High) Range(float health01)
    {
        var low = float.MaxValue;
        var high = 0f;
        for (var i = 0; i < SamplesPerPeriod; i++)
        {
            var value = LowHealthVignette.Intensity(health01, i * Period / SamplesPerPeriod, false, false);
            low = MathF.Min(low, value);
            high = MathF.Max(high, value);
        }
        return (low, high);
    }
}
