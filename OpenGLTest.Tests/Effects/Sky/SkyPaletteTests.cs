using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The sky palette and its C# reference sampler (the GLSL SkySample twins transcribe it):
/// relations only, so re-tuning a color never breaks a test.
/// </summary>
public sealed class SkyPaletteTests
{
    private const float Eps = 1e-5f;

    // Replicates RenderManager.SetLighting's sun orbit and the terms it derives from it.
    private static Vector3 SunDirAt(float angle) =>
        Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));

    private static SkyPalette PaletteFor(Vector3 sunDir)
    {
        var e = sunDir.Y;
        var dayness = MathUtils.SmoothStep((e + 0.08f) / 0.33f);
        var dusk = MathUtils.Clamp01(1f - MathF.Abs(e) * 5f);
        return SkyPalette.Compute(dayness, dusk, SkyPalette.GlowAmount(e));
    }

    // The sun's azimuth as a horizontal unit direction.
    private static Vector3 TowardSun(Vector3 sun, float y = 0f) =>
        Horizontal(MathF.Atan2(sun.Z, sun.X), y);

    private static Vector3 AwayFromSun(Vector3 sun, float y = 0f) =>
        Horizontal(MathF.Atan2(sun.Z, sun.X) + MathF.PI, y);

    private static Vector3 Horizontal(float azimuth, float y = 0f) =>
        new(MathF.Cos(azimuth) * MathF.Sqrt(1f - y * y), y, MathF.Sin(azimuth) * MathF.Sqrt(1f - y * y));

    private static void Near(Vector3 expected, Vector3 actual, float eps = Eps)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, eps);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, eps);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, eps);
    }

    [Theory]
    [InlineData(MathF.PI / 2f)] // noon
    [InlineData(MathF.PI)]      // sunset
    [InlineData(0f)]            // sunrise
    public void BelowHorizonSamplesEqualTheHorizonAtTheSameAzimuth(float angle)
    {
        var sun = SunDirAt(angle);
        var sky = PaletteFor(sun);

        for (var az = 0f; az < MathF.Tau; az += 0.4f)
        {
            var horizon = sky.Sample(Horizontal(az), sun);
            foreach (var y in new[] { -0.01f, -0.3f, -0.8f, -0.999f })
                Near(horizon, sky.Sample(Horizontal(az, y), sun));
        }
    }

    [Theory]
    [InlineData(MathF.PI / 2f)]
    [InlineData(MathF.PI)]
    [InlineData(-MathF.PI / 2f)]
    public void SampleIsContinuousAcrossTheHorizon(float angle)
    {
        var sun = SunDirAt(angle);
        var sky = PaletteFor(sun);

        for (var az = 0f; az < MathF.Tau; az += 0.4f)
            Near(sky.Sample(Horizontal(az, -1e-4f), sun), sky.Sample(Horizontal(az, 1e-4f), sun), 1e-3f);
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void DuskWarmsTheSky(float dayness)
    {
        var calm = SkyPalette.Compute(dayness, 0f).Horizon;
        var dusk = SkyPalette.Compute(dayness, 1f).Horizon;

        Assert.True(dusk.X / dusk.Z > calm.X / calm.Z);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(0.7f)]
    [InlineData(1f)]
    public void NightIsDarkerThanDay(float dusk)
    {
        var night = SkyPalette.Compute(0f, dusk);
        var day = SkyPalette.Compute(1f, dusk);

        Assert.True(night.Horizon.X < day.Horizon.X && night.Horizon.Y < day.Horizon.Y && night.Horizon.Z < day.Horizon.Z);
        Assert.True(night.Zenith.X < day.Zenith.X && night.Zenith.Y < day.Zenith.Y && night.Zenith.Z < day.Zenith.Z);
    }

    [Fact]
    public void TheSunsetGlowIsGoneByDayAndByNight()
    {
        for (var e = -1f; e <= 1f; e += 0.005f)
        {
            if (e > WorldGenerationConfig.SkyGlowBelowSunElevation && e < WorldGenerationConfig.SkyGlowAboveSunElevation)
                continue;
            Assert.Equal(0f, SkyPalette.GlowAmount(e));
        }
    }

    [Fact]
    public void TheSunsetGlowRisesToItsPeakOnTheHorizonAndFallsAwayOnBothSides()
    {
        var peak = WorldGenerationConfig.SkyGlowPeakSunElevation;
        Assert.Equal(1f, SkyPalette.GlowAmount(peak), 5);
        var previous = 0f;
        for (var e = WorldGenerationConfig.SkyGlowBelowSunElevation; e <= peak; e += 0.001f)
        {
            var g = SkyPalette.GlowAmount(e);
            Assert.True(g >= previous - 1e-6f, $"the glow fell from {previous} to {g} as the sun rose to the horizon ({e})");
            previous = g;
        }

        previous = 1f;
        for (var e = peak; e <= WorldGenerationConfig.SkyGlowAboveSunElevation; e += 0.001f)
        {
            var g = SkyPalette.GlowAmount(e);
            Assert.True(g <= previous + 1e-6f, $"the glow rose from {previous} to {g} as the sun climbed ({e})");
            previous = g;
        }
    }

    [Theory]
    [InlineData(MathF.PI / 2f)]   // noon
    [InlineData(-MathF.PI / 2f)]  // midnight
    public void AwayFromTheHorizonCrossingTheSkyLooksTheSameWhicheverWayYouTurn(float angle)
    {
        var sun = SunDirAt(angle);
        var sky = PaletteFor(sun);
        foreach (var y in new[] { 0f, 0.1f, 0.5f })
        {
            var reference = sky.Sample(Horizontal(0f, y), sun);
            for (var az = 0f; az < MathF.Tau; az += 0.3f)
                Near(reference, sky.Sample(Horizontal(az, y), sun));
        }
    }

    [Theory]
    [InlineData(MathF.PI)]  // sunset
    [InlineData(0f)]        // sunrise
    public void WithTheSunOnTheHorizonTheLowSkyTowardItIsWarmerThanAwayFromIt(float angle)
    {
        var sun = SunDirAt(angle);
        var sky = PaletteFor(sun);
        var toward = sky.Sample(TowardSun(sun, 0.02f), sun);
        var away = sky.Sample(AwayFromSun(sun, 0.02f), sun);

        Assert.True(toward.X > away.X, "the glow does not brighten the red toward the sun");
        Assert.True(toward.X / toward.Z > away.X / away.Z, "the low sky toward the sun is not warmer than away from it");
        // It is a band at the horizon: the zenith carries none of it
        Near(sky.Sample(Vector3.UnitY, sun), sky.Zenith);
    }
}
