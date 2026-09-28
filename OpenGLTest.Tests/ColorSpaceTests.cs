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
/// <see cref="Tonemap"/> is the C# twin of the post pass's max-channel Reinhard shoulder:
/// identity below the knee, C1 at it, bounded below 1 above it and hue-preserving.
/// </summary>
public sealed class TonemapTests
{
    private static readonly float[] Knees = { 0.5f, 0.7f, 0.8f, 0.9f };

    [Fact]
    public void ShoulderIsIdentityBelowKnee()
    {
        foreach (float k in Knees)
            for (int i = 0; i <= 100; i++)
            {
                float m = k * i / 100f;
                Assert.Equal(m, Tonemap.Shoulder(m, k));
            }
    }

    [Fact]
    public void ShoulderIsIncreasingAndBoundedBelowOne()
    {
        foreach (float k in Knees)
        {
            float prev = Tonemap.Shoulder(0f, k);
            for (int i = 1; i <= 1000; i++)
            {
                float m = i * 0.01f; // up to 10x over-bright, where float steps still resolve
                float v = Tonemap.Shoulder(m, k);
                Assert.True(v > prev, $"knee {k}: not increasing at {m}");
                Assert.True(v < 1f, $"knee {k}: reached 1 at {m}");
                prev = v;
            }
            // Far past that the curve flattens below float resolution; it must still never
            // fall back or reach 1.
            for (float m = 10f; m <= 1000f; m *= 1.5f)
            {
                float v = Tonemap.Shoulder(m, k);
                Assert.True(v >= prev && v < 1f, $"knee {k}: {v} at {m}");
                prev = v;
            }
        }
    }

    [Fact]
    public void ShoulderValueAndSlopeAreContinuousAtKnee()
    {
        const float h = 1e-3f;
        foreach (float k in Knees)
        {
            Assert.InRange(Tonemap.Shoulder(k + 1e-6f, k), k - 1e-5f, k + 1e-5f);
            float below = (Tonemap.Shoulder(k, k) - Tonemap.Shoulder(k - h, k)) / h;
            float above = (Tonemap.Shoulder(k + h, k) - Tonemap.Shoulder(k, k)) / h;
            Assert.InRange(below, 1f - 1e-3f, 1f + 1e-3f);
            // The shoulder's slope is 1 / (1 + x)^2, so over one step h it dips by about 2h / (1 - k).
            Assert.InRange(above, 1f - 2f * h / (1f - k) - 1e-3f, 1f + 1e-3f);
        }
    }

    [Fact]
    public void ApplyPreservesHueOfOverBrightInput()
    {
        var hdr = new Vector3(3f, 1.5f, 0.3f);
        Vector3 c = Tonemap.Apply(hdr, 1f, 0.8f);
        Assert.True(c.X < 1f);
        Assert.InRange(c.Y / c.X, 0.5f - 1e-5f, 0.5f + 1e-5f);
        Assert.InRange(c.Z / c.X, 0.1f - 1e-5f, 0.1f + 1e-5f);
    }

    [Fact]
    public void ApplyLeavesSubKneeInputUnchanged()
    {
        var v = new Vector3(0.7f, 0.4f, 0.05f);
        Assert.Equal(v, Tonemap.Apply(v, 1f, 0.8f));
    }
}
