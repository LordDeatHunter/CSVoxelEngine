using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The world's key light (<see cref="Moonlight"/>): the sun by day, the moon at night, handed over
/// with both dark so nothing pops, and only once the sunset glow the shaders' sky sample reads is gone.
/// </summary>
public sealed class MoonlightTests
{
    private static float Switch => WorldGenerationConfig.MoonlightSwitchSunElevation;

    // The key light's strength as a share of its own peak, on either side of the handover.
    private static float KeyShare(float sunElevation, float phase) => Moonlight.IsMoonLit(sunElevation)
        ? Moonlight.Intensity(sunElevation, phase) / WorldGenerationConfig.MoonlightIntensity
        : Moonlight.SunFade(sunElevation);

    [Fact]
    public void TheMoonOnlyTakesOverOnceTheSunsetGlowIsGone()
    {
        for (var e = -1f; e <= 1f; e += 0.0005f)
            if (Moonlight.IsMoonLit(e))
                Assert.Equal(0f, SkyPalette.GlowAmount(e));
    }

    [Fact]
    public void TheHandoverHappensWithBothLightsDark()
    {
        Assert.Equal(0f, Moonlight.SunFade(Switch));
        Assert.Equal(0f, Moonlight.Rise(Switch));
        foreach (var phase in new[] { 0f, 0.25f, 0.5f, 0.8f })
            Assert.Equal(0f, Moonlight.Intensity(Switch, phase));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void TheKeyLightNeverJumpsAsTheSunGoesDownAndComesBack(float phase)
    {
        const float step = 0.0005f;
        var previous = KeyShare(1f, phase);
        for (var e = 1f; e >= -1f; e -= step)
        {
            var share = KeyShare(e, phase);
            Assert.True(MathF.Abs(share - previous) < 0.01f, $"the key light jumped from {previous} to {share} at sun elevation {e}");
            previous = share;
        }
    }

    [Fact]
    public void TheSunAtOrAboveTheHorizonKeepsAllItsLight()
    {
        for (var e = 0f; e <= 1f; e += 0.01f)
            Assert.Equal(1f, Moonlight.SunFade(e));
    }

    [Fact]
    public void TheWorldIsLitByTheSunByDayAndFromAboveTheHorizonByTheMoonAtNight()
    {
        for (var t = 0.0; t < WorldGenerationConfig.DayLengthSeconds; t += 2.0)
        {
            var sun = DayCycle.SunDirection(t);
            var key = Moonlight.Direction(sun);
            if (!Moonlight.IsMoonLit(sun.Y))
            {
                Assert.Equal(sun, key);
                continue;
            }

            Assert.Equal(-sun, key);
            Assert.True(key.Y > 0f, $"the moonlight came from below the horizon at t = {t}");
        }
    }

    [Fact]
    public void AFullerMoonGivesMoreLightButANewMoonStillSome()
    {
        Assert.True(Moonlight.PhaseShare(0f) > 0f);
        Assert.Equal(1f, Moonlight.PhaseShare(0.5f), 5);
        var previous = 0f;
        for (var f = 0f; f <= 0.5f; f += 0.01f)
        {
            var share = Moonlight.PhaseShare(f);
            Assert.True(share >= previous, $"the moonlight fell as the moon waxed ({f})");
            previous = share;
        }
    }

    [Fact]
    public void TheMoonlightComesInAsTheMoonClimbs()
    {
        var previous = 0f;
        for (var e = Switch; e >= -1f; e -= 0.005f)
        {
            var i = Moonlight.Intensity(e, 0.5f);
            Assert.True(i >= previous, $"the moonlight weakened as the moon climbed (sun at {e})");
            previous = i;
        }

        Assert.Equal(WorldGenerationConfig.MoonlightIntensity, previous, 5);
    }

    [Fact]
    public void MoonlightIsACoolLight()
    {
        Assert.True(Moonlight.ColorSrgb.Z > Moonlight.ColorSrgb.Y && Moonlight.ColorSrgb.Y > Moonlight.ColorSrgb.X);
    }
}
