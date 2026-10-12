using System;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The damage chromatic fringe (<see cref="DamageFringe"/>, the twin of post-fragment.glsl's): driven
/// by the vignette's damage flash, it shows only for its first moment after a hit and only toward the
/// screen edges.
/// </summary>
public sealed class DamageFringeTests
{
    [Fact]
    public void ThereIsNoFringeWithoutAHitAndAFullOneRightAtIt()
    {
        Assert.Equal(0f, DamageFringe.Strength(0f));
        Assert.Equal(1f, DamageFringe.Strength(1f), 5);
    }

    [Fact]
    public void TheFringeLastsOnlyItsShortMomentOfTheDamageFlash()
    {
        var health = new PlayerHealth();
        health.TakeDamage(10);
        const float step = 1f / 120f;
        var seconds = 0f;
        var previous = DamageFringe.Strength(health.DamageFlash);
        while (seconds < PlayerHealth.DamageFlashSeconds)
        {
            var fringe = DamageFringe.Strength(health.DamageFlash);
            Assert.True(fringe <= previous, "the fringe grew back while the flash faded");
            if (seconds < WorldGenerationConfig.DamageFringeSeconds - 2f * step)
                Assert.True(fringe > 0f, $"the fringe was gone {seconds} s after the hit");
            if (seconds > WorldGenerationConfig.DamageFringeSeconds + 2f * step)
                Assert.Equal(0f, fringe);
            previous = fringe;
            health.Update(step);
            seconds += step;
        }

        // The vignette's flash itself lasts much longer: the fringe is only its first moment
        Assert.True(WorldGenerationConfig.DamageFringeSeconds < PlayerHealth.DamageFlashSeconds);
    }

    [Fact]
    public void TheFringeLeavesTheCentreAloneAndIsStrongestAtTheCorners()
    {
        Assert.Equal(0f, DamageFringe.EdgeWeight(0f));
        Assert.Equal(1f, DamageFringe.EdgeWeight(MathF.Sqrt(0.5f)), 5);
        var previous = 0f;
        for (var d = 0f; d <= 0.71f; d += 0.01f)
        {
            var w = DamageFringe.EdgeWeight(d);
            Assert.True(w >= previous, $"the fringe weakened toward the edge ({d})");
            previous = w;
        }
    }
}
