using VoxelEngine.Effects.Surfaces;
using Xunit;

namespace VoxelEngine.Tests.Effects.Surfaces;

/// <summary>
/// The leaf backlight (<see cref="LeafBacklight"/>, the twin of the term in test-fragment.glsl):
/// leaves glow only between the eye and a risen sun, and only where sunlight reaches them.
/// </summary>
public sealed class LeafBacklightTests
{
    private const float HighSun = 0.5f;
    private const float DaySun = 2f;

    [Fact]
    public void TheGlowGrowsAsTheViewTurnsTowardTheSun()
    {
        var previous = LeafBacklight.Weight(-1f, HighSun, DaySun, 1f);
        for (var forward = -1f; forward <= 1f; forward += 0.001f)
        {
            var w = LeafBacklight.Weight(forward, HighSun, DaySun, 1f);
            Assert.True(w >= previous, $"glow fell from {previous} to {w} turning toward the sun ({forward})");
            previous = w;
        }

        Assert.True(LeafBacklight.Weight(1f, HighSun, DaySun, 1f) > 0f);
    }

    [Fact]
    public void ThereIsNoGlowLookingAwayFromTheSunOrSquareToIt()
    {
        foreach (var forward in new[] { -1f, -0.5f, 0f })
            Assert.Equal(0f, LeafBacklight.Weight(forward, HighSun, DaySun, 1f));
    }

    [Fact]
    public void LeavesTheSunCannotReachStayDark()
    {
        Assert.Equal(0f, LeafBacklight.Weight(1f, HighSun, DaySun, 0f));
        var previous = 0f;
        for (var through = 0f; through <= 1f; through += 0.05f)
        {
            var w = LeafBacklight.Weight(1f, HighSun, DaySun, through);
            Assert.True(w >= previous, $"glow fell from {previous} to {w} with more light through ({through})");
            previous = w;
        }
    }

    [Fact]
    public void ThereIsNoGlowWithTheSunAtOrBelowTheHorizon()
    {
        for (var elevation = -1f; elevation <= 0f; elevation += 0.05f)
            Assert.Equal(0f, LeafBacklight.Weight(1f, elevation, DaySun, 1f));
        Assert.Equal(0f, LeafBacklight.Weight(1f, HighSun, 0f, 1f));
    }

    [Fact]
    public void TheGlowNeverExceedsItsStrength()
    {
        for (var forward = -1f; forward <= 1f; forward += 0.1f)
        for (var elevation = -1f; elevation <= 1f; elevation += 0.1f)
        for (var through = 0f; through <= 1.5f; through += 0.25f)
            Assert.InRange(LeafBacklight.Weight(forward, elevation, DaySun, through),
                0f, WorldGenerationConfig.LeafBacklightStrength);
    }
}
