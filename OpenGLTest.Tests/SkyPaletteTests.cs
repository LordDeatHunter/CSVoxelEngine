using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The sky palette and its C# reference sampler (the GLSL SkySample twins transcribe it):
/// relations only, so re-tuning a colour never breaks a test.
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
        return SkyPalette.Compute(dayness, dusk);
    }

    private static Vector3 Horizontal(float azimuth, float y = 0f) =>
        new(MathF.Cos(azimuth) * MathF.Sqrt(1f - y * y), y, MathF.Sin(azimuth) * MathF.Sqrt(1f - y * y));

    private static void Near(Vector3 expected, Vector3 actual, float eps = Eps)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, eps);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, eps);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, eps);
    }

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    [Fact]
    public void NoonHasNoGlowAndSamplesHitZenithAndHazedHorizon()
    {
        var sun = SunDirAt(MathF.PI / 2f);
        var sky = PaletteFor(sun);

        Assert.Equal(Vector3.Zero, sky.Glow);
        Near(sky.Zenith, sky.Sample(Vector3.UnitY, sun));
        for (var az = 0f; az < MathF.Tau; az += 0.7f)
            Near(sky.HazedHorizon, sky.Sample(Horizontal(az), sun));
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
    [InlineData(MathF.PI)] // sunset: sun on the -X horizon
    [InlineData(0f)]       // sunrise
    public void SunsetGlowPeaksTowardTheSunAzimuth(float angle)
    {
        var sun = SunDirAt(angle);
        var sky = PaletteFor(sun);
        var toward = Vector3.Normalize(new Vector3(sun.X, 0f, sun.Z));
        var away = -toward;

        var towardSample = sky.Sample(toward, sun);
        var awaySample = sky.Sample(away, sun);

        Assert.True(towardSample.X > awaySample.X);
        Assert.True(Luminance(towardSample) > Luminance(awaySample));
        Near(sky.HazedHorizon, awaySample);
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
    public void CloudSunWeightGrowsWithDuskAndClamps()
    {
        var previous = float.NegativeInfinity;
        foreach (var dusk in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var w = SkyPalette.CloudLightWeights(dusk);
            var ratio = w.Y / w.X;
            Assert.True(ratio > previous);
            previous = ratio;
        }

        Assert.Equal(SkyPalette.CloudLightWeights(0f), SkyPalette.CloudLightWeights(-1f));
        Assert.Equal(SkyPalette.CloudLightWeights(1f), SkyPalette.CloudLightWeights(2f));
    }
}
