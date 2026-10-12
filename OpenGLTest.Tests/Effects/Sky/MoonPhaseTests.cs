using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The moon (<see cref="MoonPhase"/>, the twin of moon-fragment.glsl's phase shading): which phase a
/// night shows, that it never changes in view, and how a phase lights the disc.
/// </summary>
public sealed class MoonPhaseTests
{
    private static double Day => WorldGenerationConfig.DayLengthSeconds;

    [Fact]
    public void ThePhaseStepsOnceADayAndRepeatsEveryCycle()
    {
        var cycle = WorldGenerationConfig.MoonCycleDays;
        for (var day = -cycle; day < 2 * cycle; day++)
        {
            var start = DayCycle.NoonTime + day * Day;
            var phase = MoonPhase.Fraction(start + 1.0);
            // Constant through the whole day
            for (var t = start + 1.0; t < start + Day - 1.0; t += Day / 37.0)
                Assert.Equal(phase, MoonPhase.Fraction(t), 6);
            // One step on to the next day, wrapping round the cycle
            var next = MoonPhase.Fraction(start + Day + 1.0);
            var step = next - phase;
            if (step < 0f) step += 1f;
            Assert.Equal(1f / cycle, step, 4);
            // And the same phase a whole cycle later
            Assert.Equal(phase, MoonPhase.Fraction(start + 1.0 + cycle * Day), 5);
        }
    }

    [Fact]
    public void ThePhaseNeverChangesWhileTheMoonIsInView()
    {
        var previousVisible = false;
        var phaseWhileVisible = 0f;
        for (var t = 0.0; t < 3 * WorldGenerationConfig.MoonCycleDays * Day; t += 2.0)
        {
            var visible = MoonPhase.Visibility(DayCycle.SunDirection(t).Y) > 0f;
            var phase = MoonPhase.Fraction(t);
            if (visible && previousVisible)
                Assert.Equal(phaseWhileVisible, phase);
            phaseWhileVisible = phase;
            previousVisible = visible;
        }
    }

    [Fact]
    public void TheMoonOnlyShowsAboveTheHorizonAndNeverByDay()
    {
        for (var t = 0.0; t < 2 * Day; t += 1.0)
        {
            var sun = DayCycle.SunDirection(t);
            if (MoonPhase.Visibility(sun.Y) <= 0f) continue;
            Assert.True(-sun.Y > 0f, $"the moon showed below the horizon at t = {t}");
            Assert.True(sun.Y < 0f, $"the moon showed with the sun up at t = {t}");
        }
    }

    [Fact]
    public void TheFirstNightOfANewWorldIsAFullMoon()
    {
        var firstMidnight = DayCycle.NoonTime + Day / 2.0;
        Assert.Equal(1f, MoonPhase.IlluminatedFraction(MoonPhase.Fraction(firstMidnight)), 5);
    }

    [Fact]
    public void AFullMoonIsLitAllOverAndANewMoonNowhere()
    {
        for (var x = -0.9f; x <= 0.9f; x += 0.1f)
        for (var y = -0.9f; y <= 0.9f; y += 0.1f)
        {
            if (x * x + y * y > 0.98f) continue;
            var n = Normal(x, y);
            Assert.Equal(1f, MoonPhase.Lit(n, 0.5f), 4);
            Assert.Equal(0f, MoonPhase.Lit(n, 0f), 4);
        }
    }

    [Fact]
    public void AWaxingMoonIsLitOnTheRightAndAWaningOneOnTheLeft()
    {
        var right = Normal(0.6f, 0f);
        var left = Normal(-0.6f, 0f);
        Assert.True(MoonPhase.Lit(right, 0.25f) > 0.99f && MoonPhase.Lit(left, 0.25f) < 0.01f);
        Assert.True(MoonPhase.Lit(left, 0.75f) > 0.99f && MoonPhase.Lit(right, 0.75f) < 0.01f);
    }

    [Fact]
    public void TheLitShareGrowsFromNewToFullAndMatchesTheShadedDisc()
    {
        var previous = -1f;
        for (var f = 0f; f <= 0.5f; f += 0.025f)
        {
            var share = MoonPhase.IlluminatedFraction(f);
            Assert.True(share >= previous, "the lit share fell while the moon waxed");
            previous = share;
            // The shader's terminator (MoonPhase.Lit) lights that share of the visible disc
            Assert.InRange(ShadedShare(f) - share, -0.03f, 0.03f);
        }
    }

    private static Vector3 Normal(float x, float y) => new(x, y, MathF.Sqrt(MathF.Max(1f - x * x - y * y, 0f)));

    // The lit share of the disc as the shader shades it, integrated over a grid.
    private static float ShadedShare(float fraction)
    {
        const int n = 160;
        float lit = 0f, total = 0f;
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            var x = (i + 0.5f) / n * 2f - 1f;
            var y = (j + 0.5f) / n * 2f - 1f;
            if (x * x + y * y >= 1f) continue;
            lit += MoonPhase.Lit(Normal(x, y), fraction);
            total += 1f;
        }

        return lit / total;
    }
}
