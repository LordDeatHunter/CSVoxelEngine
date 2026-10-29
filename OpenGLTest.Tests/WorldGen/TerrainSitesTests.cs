using System;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The shared hashed-site rules (<see cref="TerrainSites"/>) lakes, massifs and lone peaks stand on:
/// the cell range that can reach a point, a roll's center inside its cell, the inward-only wobble and
/// the rim spread.
/// </summary>
public class TerrainSitesTests
{
    private const int Salt = 0x54455354;

    public TerrainSitesTests() => TestContent.EnsureLoaded();

    [Fact]
    public void CellRangeCoversEveryCellThatCanReachAPoint()
    {
        var rng = new SplitMix64(0xC0FFEE);
        var excluded = 0;
        foreach (var cell in new[] { 64, 192, 448 })
        {
            for (var i = 0; i < 2000; i++)
            {
                var v = rng.NextFloat(-20000f, 20000f);
                var reach = rng.NextFloat(0f, 2f * cell);
                TerrainSites.CellRange(v, cell, reach, out var min, out var max);

                var home = (int)MathF.Floor(v / cell);
                for (var g = home - 3; g <= home + 3; g++)
                {
                    // Distance from v to the cell's block span [g * cell, (g + 1) * cell).
                    var distance = MathF.Max(0f, MathF.Max(g * cell - v, v - (g + 1) * cell));
                    if (distance > reach)
                    {
                        if (g < min || g > max) excluded++;
                        continue;
                    }

                    Assert.True(g >= min && g <= max,
                        $"cell {g} (size {cell}) reaches {v} within {reach} but the range is {min}..{max}");
                }
            }
        }

        // The range is not just the whole neighborhood: it leaves out cells that cannot reach.
        Assert.True(excluded > 0);
    }

    [Fact]
    public void ARolledCandidateLiesInsideItsCell()
    {
        const int cell = 192;
        var hits = 0;
        var misses = 0;
        for (var gx = -20; gx <= 20; gx++)
        for (var gz = -20; gz <= 20; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cell, Salt, 0.5f, 2f, out var hash, out var cx, out var cz,
                    out var size))
            {
                misses++;
                Assert.Equal(0UL, hash);
                Assert.Equal(0f, cx);
                Assert.Equal(0f, cz);
                Assert.Equal(0f, size);
                continue;
            }

            hits++;
            // The upper bound is inclusive: a roll a hair under 1 can round onto the next cell's edge.
            Assert.InRange(cx, (float)(gx * cell), (float)((gx + 1) * cell));
            Assert.InRange(cz, (float)(gz * cell), (float)((gz + 1) * cell));
            Assert.InRange(size, 0f, 1f);
        }

        Assert.True(hits > 0);
        Assert.True(misses > 0);
    }

    [Fact]
    public void InwardWobbleNeverExceedsItsNominal()
    {
        const float nominal = 100f;
        const float amplitude = 0.3f;
        var noise = TerrainNoise.LakeShapeNoise;
        var outward = false;
        for (var x = -3000; x <= 3000; x += 7)
        for (var z = -3000; z <= 3000; z += 7)
        {
            var inward = TerrainSites.InwardWobble(nominal, amplitude, noise, x, z);
            Assert.InRange(inward, nominal * (1f - amplitude), nominal);
            if (TerrainSites.Wobble(nominal, amplitude, noise, x, z) > nominal) outward = true;
        }

        // The plain wobble does push past the nominal radius, which is why the inward one exists.
        Assert.True(outward);
    }

    [Fact]
    public void RimRangeBracketsEverySample()
    {
        static float Height(float x, float z) =>
            MathF.Sin(x * 0.031f) * MathF.Cos(z * 0.017f) * 40f + x * 0.01f - z * 0.02f;

        var rng = new SplitMix64(0x51DE);
        for (var i = 0; i < 500; i++)
        {
            var cx = rng.NextFloat(-5000f, 5000f);
            var cz = rng.NextFloat(-5000f, 5000f);
            var radius = rng.NextFloat(1f, 400f);
            TerrainSites.RimRange(cx, cz, radius, Height, out var min, out var max);

            var center = Height(cx, cz);
            Assert.InRange(center, min, max);
            var sawMin = center == min;
            var sawMax = center == max;
            for (var k = 0; k < TerrainSites.RimPoints; k++)
            {
                TerrainSites.RimPoint(cx, cz, radius, k, out var x, out var z);
                var h = Height(x, z);
                Assert.InRange(h, min, max);
                sawMin |= h == min;
                sawMax |= h == max;
            }

            // Both bounds are samples, not loose envelopes.
            Assert.True(sawMin && sawMax);
        }
    }
}
