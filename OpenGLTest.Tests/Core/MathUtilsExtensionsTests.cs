using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MathUtilsExtensionsTests
{
    [Theory]
    [InlineData(-200f, -89f)]
    [InlineData(-1f, -1f)]
    [InlineData(0f, 0f)]
    [InlineData(200f, 89f)]
    public void ClampPitchMatchesMouseLook(float pitch, float expected) =>
        Assert.Equal(expected, MathUtils.ClampPitch(pitch));

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(90f, 0f)]
    [InlineData(180f, -1f)]
    public void CosDegMatchesRadians(float degrees, float expected) =>
        Assert.Equal(expected, MathUtils.CosDeg(degrees), precision: 5);

    [Fact]
    public void ToRadiansRoundTrips() =>
        Assert.Equal(45f, MathUtils.ToDegrees(MathUtils.ToRadians(45f)), precision: 5);

    [Theory]
    [InlineData(1.9f, 1)]
    [InlineData(-0.1f, -1)]
    [InlineData(-16.0f, -16)]
    public void FloorToIntHandlesNegatives(float value, int expected) =>
        Assert.Equal(expected, MathUtils.FloorToInt(value));

    [Fact]
    public void SnapFloorsToField() =>
        Assert.Equal(32f, MathUtils.Snap(47f, 16f));

    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(15, 16, 0)]
    [InlineData(16, 16, 1)]
    [InlineData(-1, 16, -1)]
    [InlineData(-16, 16, -1)]
    [InlineData(-17, 16, -2)]
    public void FloorDivRoundsTowardNegativeInfinity(int a, int b, int expected) =>
        Assert.Equal(expected, MathUtils.FloorDiv(a, b));

    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(15, 16, 15)]
    [InlineData(16, 16, 0)]
    [InlineData(-1, 16, 15)]
    [InlineData(-17, 16, 15)]
    public void ModStaysPositive(int a, int b, int expected) =>
        Assert.Equal(expected, MathUtils.Mod(a, b));

    [Theory]
    [InlineData(5f, 2f, 2f)]
    [InlineData(-5f, 2f, -2f)]
    [InlineData(1f, 2f, 1f)]
    public void ClampStepLimitsPerAxisMovement(float remaining, float step, float expected) =>
        Assert.Equal(expected, MathUtils.ClampStep(remaining, step));

    [Fact]
    public void DampAngleTakesShortWayAround()
    {
        // 350 -> 10 wraps forward through 360 (result 370 == 10 mod 360), and back.
        var forward = MathUtils.DampAngle(350f, 10f, 1000f, 1f);
        Assert.Equal(10f, MathUtils.Mod(MathUtils.FloorToInt(forward + 0.5f), 360), precision: 3);
        var back = MathUtils.DampAngle(10f, 350f, 1000f, 1f);
        Assert.Equal(350f, MathUtils.Mod(MathUtils.FloorToInt(back + 360.5f), 360), precision: 3);
        Assert.True(forward > 350f, "Should advance forward through 360, not spin backwards.");
        Assert.True(back < 10f, "Should retreat through 0, not spin forwards.");
    }

    [Fact]
    public void DampAngleConvergesWithoutOvershoot()
    {
        var yaw = 0f;
        for (var i = 0; i < 300; i++)
            yaw = MathUtils.DampAngle(yaw, 90f, 5f, 1f / 60f);
        Assert.Equal(90f, yaw, precision: 1);
    }

    [Fact]
    public void HoldThenFadeHoldsFullyThenOnlyFallsToZero()
    {
        const float hold = 0.8f, fade = 0.4f;
        var previous = 1f;
        for (var t = 0f; t <= hold + fade + 0.2f; t += 0.01f)
        {
            var alpha = MathUtils.HoldThenFade(t, hold, fade);
            if (t <= hold) Assert.Equal(1f, alpha);
            Assert.InRange(alpha, 0f, 1f);
            Assert.True(alpha <= previous, $"alpha rose at t={t}");
            previous = alpha;
        }
        Assert.Equal(0f, MathUtils.HoldThenFade(hold + fade, hold, fade));
    }

    [Fact]
    public void HeartbeatRepeatsEveryPeriodWithinZeroToOne()
    {
        const float period = 1.3f;
        var peak = 0f;
        for (var t = 0f; t < 3f * period; t += 0.005f)
        {
            var beat = MathUtils.Heartbeat(t, period);
            Assert.InRange(beat, 0f, 1f);
            Assert.True(MathF.Abs(beat - MathUtils.Heartbeat(t + period, period)) < 0.01f,
                $"the beat at t={t} differs one period later");
            peak = MathF.Max(peak, beat);
        }
        Assert.True(peak > 0.99f, "a beat should reach its full height once a cycle");
    }

    [Fact]
    public void BackPulseRisesToOneAndLandsExactlyOnZero()
    {
        const float peak = 0.3f;
        Assert.Equal(0f, MathUtils.BackPulse(0f, peak));
        Assert.Equal(1f, MathUtils.BackPulse(peak, peak), precision: 5);
        Assert.Equal(0f, MathUtils.BackPulse(1f, peak));
        Assert.Equal(0f, MathUtils.BackPulse(2f, peak));

        // The rise never falls back before the peak
        var previous = 0f;
        for (var t = 0f; t < peak; t += 0.01f)
        {
            var value = MathUtils.BackPulse(t, peak);
            Assert.True(value >= previous, $"rise fell at t={t}");
            previous = value;
        }
    }
}
