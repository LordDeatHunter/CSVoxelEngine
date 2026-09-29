using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="ColorSpace"/> is the exact sRGB transfer pair the linear pipeline decodes CPU
/// colours with; the GLSL twins in the post and UI shaders mirror its encode.
/// </summary>
public sealed class ColorSpaceTests
{
    private static float Sample(int i, int n, float max = 1f) => max * i / n;

    [Fact]
    public void DecodeAndEncodeRoundTrip()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float x = Sample(i, 1000);
            Assert.InRange(ColorSpace.LinearToSrgb(ColorSpace.SrgbToLinear(x)), x - 1e-4f, x + 1e-4f);
            Assert.InRange(ColorSpace.SrgbToLinear(ColorSpace.LinearToSrgb(x)), x - 1e-4f, x + 1e-4f);
        }
    }

    [Fact]
    public void DecodeIsStrictlyIncreasingIntoHdrRange()
    {
        float prev = ColorSpace.SrgbToLinear(0f);
        for (int i = 1; i <= 2000; i++)
        {
            float v = ColorSpace.SrgbToLinear(Sample(i, 2000, 2f));
            Assert.True(v > prev, $"not increasing at {Sample(i, 2000, 2f)}");
            prev = v;
        }
    }

    [Fact]
    public void BranchesMeetAtThresholdsAndEndpointsAreFixed()
    {
        const float eps = 1e-6f;
        Assert.True(MathF.Abs(ColorSpace.SrgbToLinear(0.04045f - eps) - ColorSpace.SrgbToLinear(0.04045f + eps)) < 1e-4f);
        Assert.True(MathF.Abs(ColorSpace.LinearToSrgb(0.0031308f - eps) - ColorSpace.LinearToSrgb(0.0031308f + eps)) < 1e-4f);

        Assert.Equal(0f, ColorSpace.SrgbToLinear(0f));
        Assert.Equal(0f, ColorSpace.LinearToSrgb(0f));
        Assert.InRange(ColorSpace.SrgbToLinear(1f), 1f - 1e-5f, 1f + 1e-5f);
        Assert.InRange(ColorSpace.LinearToSrgb(1f), 1f - 1e-5f, 1f + 1e-5f);
    }

    [Fact]
    public void DecodingDarkensMidTones()
    {
        for (int i = 1; i < 90; i++)
        {
            float c = 0.05f + i * 0.01f;
            Assert.True(ColorSpace.SrgbToLinear(c) < c, $"{c} did not darken");
        }
    }

    [Fact]
    public void EncodeClampsNegativesToBlack() =>
        Assert.Equal(0f, ColorSpace.LinearToSrgb(-0.5f));

    [Fact]
    public void VectorOverloadsMatchScalars()
    {
        var v = new Vector3(0.02f, 0.5f, 1.2f);
        Assert.Equal(new Vector3(ColorSpace.SrgbToLinear(v.X), ColorSpace.SrgbToLinear(v.Y), ColorSpace.SrgbToLinear(v.Z)),
            ColorSpace.SrgbToLinear(v));
        Assert.Equal(new Vector3(ColorSpace.LinearToSrgb(v.X), ColorSpace.LinearToSrgb(v.Y), ColorSpace.LinearToSrgb(v.Z)),
            ColorSpace.LinearToSrgb(v));
    }
}

/// <summary>
/// <see cref="Tonemap"/> is the C# twin of the post pass's tonemap: a per-channel clamp after
/// exposure, so in-range colour passes through and each over-bright channel clips on its own.
/// </summary>
public sealed class TonemapTests
{
    [Fact]
    public void InRangeInputPassesThroughUnchanged()
    {
        var v = new Vector3(0.7f, 0.4f, 0.05f);
        Assert.Equal(v, Tonemap.Apply(v, 1f));
    }

    [Fact]
    public void EachOverBrightChannelClipsOnItsOwn()
    {
        // An over-bright warm colour clips toward white/yellow like the old 8-bit target,
        // instead of keeping its ratios: the clipped channels meet at 1, the rest are untouched.
        var hdr = new Vector3(3f, 1.5f, 0.3f);
        Vector3 c = Tonemap.Apply(hdr, 1f);
        Assert.Equal(1f, c.X);
        Assert.Equal(c.X, c.Y);
        Assert.Equal(hdr.Z, c.Z);
    }

    [Fact]
    public void OutputStaysInDisplayRangeAndIsMonotonic()
    {
        float prev = -1f;
        for (int i = -10; i <= 400; i++)
        {
            float m = i * 0.01f;
            float v = Tonemap.Apply(new Vector3(m), 1f).X;
            Assert.InRange(v, 0f, 1f);
            Assert.True(v >= prev, $"not monotonic at {m}");
            prev = v;
        }
    }

    [Fact]
    public void ExposureScalesBeforeTheClamp()
    {
        var v = new Vector3(0.2f, 0.1f, 0.05f);
        Assert.Equal(Tonemap.Apply(v * 2f, 1f), Tonemap.Apply(v, 2f));
    }
}
