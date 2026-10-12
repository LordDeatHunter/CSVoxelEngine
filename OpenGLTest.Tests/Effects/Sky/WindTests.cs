using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>SH-27: the shared wind is a unit heading that turns slowly, and its traveled offset is its integral.</summary>
public sealed class WindTests
{
    private static readonly double[] Times = [0.0, 1.0, 147.0, 447.0, 1200.0, 9999.5, 1e6];

    [Fact]
    public void DirectionIsUnit()
    {
        foreach (var t in Times)
            Assert.Equal(1f, Wind.Direction(t).Length(), 4);
    }

    [Fact]
    public void DirectionTurnsSlowly()
    {
        // Over a second the heading barely moves, yet over a long stretch it really does turn
        foreach (var t in Times)
            Assert.True(Vector2.Dot(Wind.Direction(t), Wind.Direction(t + 1.0)) > 0.999f);
        var longest = double.MaxValue;
        for (var t = 0.0; t < 3000.0; t += 100.0)
            longest = Math.Min(longest, Vector2.Dot(Wind.Direction(0.0), Wind.Direction(t)));
        Assert.True(longest < 0f);
    }

    [Fact]
    public void OffsetDerivativeMatchesDirection()
    {
        const float speed = 3f;
        const double h = 0.01;
        foreach (var t in new[] { 0.5, 147.0, 447.0, 1200.0 })
        {
            var rate = (Wind.Offset(t + h, speed) - Wind.Offset(t - h, speed)) / (float)(2 * h);
            var expected = Wind.Direction(t) * speed;
            Assert.True(Vector2.Distance(rate, expected) < 0.01f * speed, $"t={t}: {rate} vs {expected}");
        }
    }

    [Fact]
    public void OffsetStartsAtZeroAndStaysBounded()
    {
        const float speed = 3f;
        Assert.True(Wind.Offset(0.0, speed).Length() < 1e-4f);
        // It traces a circle, so it never gets further than one diameter from the start
        var diameter = 2.0 * speed / (WorldGenerationConfig.WindTurnDegreesPerSecond * Math.PI / 180.0);
        var furthest = 0f;
        for (var t = 0.0; t < 20000.0; t += 37.0)
            furthest = MathF.Max(furthest, Wind.Offset(t, speed).Length());
        Assert.True(furthest <= diameter * 1.0001);
        Assert.True(furthest > 0.5 * diameter); // anti-vacuity: it really travels
    }

    [Fact]
    public void AGustNeverReversesTheWindNorBlowsPastFullStrengthAndVariesAcrossTheWorld()
    {
        var weakest = 1f;
        var strongest = 0f;
        foreach (var t in Times)
        {
            var offset = Wind.Offset(t, WorldGenerationConfig.WindGustSpeed);
            for (var x = -200f; x <= 200f; x += 7.3f)
            {
                var anchor = new Vector2(x, x * 0.37f - 11f);
                var gust = Wind.Gust(anchor, t, offset);
                Assert.InRange(gust, 0.01f, 1f);
                // The precomputed-offset overload is the very phase the shader twin computes
                Assert.Equal(Wind.GustPhase(anchor, t), Wind.GustPhase(anchor, t, offset), 3);
                weakest = MathF.Min(weakest, gust);
                strongest = MathF.Max(strongest, gust);
            }
        }

        Assert.True(strongest - weakest > 0.3f, "the gusts never vary");
    }

    [Fact]
    public void GustPhaseRateIsIndependentOfDistanceFromOrigin()
    {
        const double h = 0.1;
        foreach (var t in new[] { 10.0, 600.0 })
        {
            float Rate(Vector2 p) => (Wind.GustPhase(p, t + h) - Wind.GustPhase(p, t - h)) / (float)(2 * h);
            Assert.Equal(Rate(Vector2.Zero), Rate(new Vector2(10000f, -10000f)), 2);
        }
    }
}
