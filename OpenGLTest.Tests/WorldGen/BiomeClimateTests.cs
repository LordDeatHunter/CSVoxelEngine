using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// Covers the one climate sample: every climate reader (the cold and warm weights, the tint
/// climate, the biome) sees exactly what <see cref="BiomeClassifier.SampleClimate"/> returns, and
/// the domain warp that sample reads through stretches the plane only boundedly and leaves the
/// origin where it is.
/// </summary>
public sealed class BiomeClimateTests
{
    public BiomeClimateTests() => TestContent.EnsureLoaded();

    [Fact]
    public void EveryClimateReaderSeesTheOneSample()
    {
        const int stride = 96;
        const int extent = 3072;
        var samples = new List<(float Temperature, float Warmth)>();
        var sawCold = false;
        var sawWarm = false;
        var sawTemperate = false;

        for (var x = -extent; x <= extent; x += stride)
        {
            for (var z = -extent; z <= extent; z += stride)
            {
                BiomeClassifier.SampleClimate(x, z, out var t, out var h);
                var cold = BiomeClassifier.ColdOf(t);
                var warm = BiomeClassifier.WarmOf(t);

                Assert.Equal(cold, TerrainGenerator.ColdWeight(x, z));
                Assert.Equal(warm, TerrainGenerator.WarmWeight(x, z));

                TerrainGenerator.Climate(x, z, out var w, out var wet);
                Assert.Equal(BiomeClassifier.WetnessOf(h), wet);

                // The warmth side as one-way implications that hold in float: a tiny positive
                // smoothstep can round 0.5 + 0.5 * warm to exactly 0.5, so warmth is never
                // asserted strictly above the midpoint from a warm sample.
                if (cold > 0f) Assert.True(w < 0.5f, $"cold sample at ({x}, {z}) gave warmth {w}");
                if (warm > 0f) Assert.True(w >= 0.5f, $"warm sample at ({x}, {z}) gave warmth {w}");
                if (w < 0.5f) Assert.True(cold > 0f, $"warmth {w} at ({x}, {z}) without cold");
                if (w > 0.5f) Assert.True(warm > 0f, $"warmth {w} at ({x}, {z}) without warmth");
                if (cold == 0f && warm == 0f) Assert.Equal(0.5f, w);
                samples.Add((t, w));

                var biome = TerrainGenerator.BiomeWeights(x, z, out var ocean, out _);
                Assert.Equal(BiomeClassifier.Classify(ocean, cold, warm,
                    BiomeClassifier.DryOf(h), BiomeClassifier.SemiDryOf(h), BiomeClassifier.JungleOf(t, h)), biome);
                Assert.Equal(biome, TerrainGenerator.BiomeAt(x, z));

                sawCold |= cold > 0f;
                sawWarm |= warm > 0f;
                sawTemperate |= cold == 0f && warm == 0f;
            }
        }

        Assert.True(sawCold, "the scan never reached the cold ramp");
        Assert.True(sawWarm, "the scan never reached the warm ramp");
        Assert.True(sawTemperate, "the scan never reached the temperate band");

        // Warmth is a function of the temperature sample alone, never decreasing with it.
        samples.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].Warmth >= samples[i - 1].Warmth,
                $"warmth fell from {samples[i - 1].Warmth} to {samples[i].Warmth} as temperature rose " +
                $"from {samples[i - 1].Temperature} to {samples[i].Temperature}");
        }
    }

    [Fact]
    public void TheWarpStretchesTheClimateBoundedly()
    {
        // The design budget: the warp may at most double a distance, so it can at most halve a
        // temperature band (WorldGenerationConfig.ClimateWarp*).
        const double maxStretch = 2.0;
        const int stride = 37;
        const int extent = 4096;
        (int X, int Z)[] steps = [(1, 0), (0, 1), (1, 1), (1, -1)];
        var sawStretch = false;
        var sawShrink = false;

        var originX = 0f;
        var originZ = 0f;
        TerrainNoise.ClimateWarp.DomainWarp(ref originX, ref originZ);
        Assert.Equal(0f, originX);
        Assert.Equal(0f, originZ);

        for (var x = -extent; x <= extent; x += stride)
        {
            for (var z = -extent; z <= extent; z += stride)
            {
                var (bx, bz) = Warp(x, z);

                // The finite-difference Jacobian's largest singular value bounds the stretch in
                // every direction at once.
                var (xx, xz) = Warp(x + 1, z);
                var (zx, zz) = Warp(x, z + 1);
                double a = xx - bx, c = xz - bz, b = zx - bx, d = zz - bz;
                var sumSquares = a * a + b * b + c * c + d * d;
                var det = a * d - b * c;
                var sigmaMax = Math.Sqrt((sumSquares +
                    Math.Sqrt(Math.Max(0.0, sumSquares * sumSquares - 4.0 * det * det))) / 2.0);
                Assert.True(sigmaMax <= maxStretch, $"the warp stretches by {sigmaMax} at ({x}, {z})");

                foreach (var (sx, sz) in steps)
                {
                    var (px, pz) = Warp(x + sx, z + sz);
                    var moved = Math.Sqrt((px - bx) * (px - bx) + (pz - bz) * (pz - bz));
                    var length = Math.Sqrt(sx * sx + sz * sz);
                    Assert.True(moved <= maxStretch * length,
                        $"a step of {length} at ({x}, {z}) along ({sx}, {sz}) moved {moved}");
                    sawStretch |= moved > length;
                    sawShrink |= moved < length;
                }
            }
        }

        Assert.True(sawStretch, "the warp never stretched a step, so it is not warping");
        Assert.True(sawShrink, "the warp never shrank a step, so it is not warping");
    }

    /// <summary>The warped position of a world column, widened to double.</summary>
    private static (double X, double Z) Warp(float x, float z)
    {
        TerrainNoise.ClimateWarp.DomainWarp(ref x, ref z);
        return (x, z);
    }
}
