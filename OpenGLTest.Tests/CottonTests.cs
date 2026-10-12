using System.Collections.Generic;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Wild cotton: the plant breaks into cotton, the generator places it only on dry grass inside its
/// clumps on the forest's desert fringe, and that fringe stays clear of the dithered sand border.
/// The clump pattern itself is <see cref="PlantClumpTests"/>.
/// </summary>
public sealed class CottonTests
{
    public CottonTests() => TestContent.EnsureLoaded();

    private static BlockType CottonPlant => ContentRegistry.RequireBlock("cotton_plant");

    [Fact]
    public void BreakingACottonPlantAlwaysYieldsCottonAndNothingElse()
    {
        var cotton = ContentRegistry.RequireItem("cotton").Id;
        var into = new List<byte>();
        for (var i = 0; i < 200; i++)
        {
            into.Clear();
            DropManager.RollLoot(CottonPlant.Id, new LootContext(3 + i, 40, -7), into);
            Assert.NotEmpty(into);
            Assert.All(into, id => Assert.Equal(cotton, id));
        }
    }

    /// <summary>The surface chunks over every gated cotton clump near the origin.</summary>
    private static List<Chunk> ChunksOverClumps(int wanted)
    {
        var spec = CottonGenerator.Clumps;
        var chunks = new Dictionary<(int, int, int), Chunk>();
        var clumps = 0;
        for (var cellX = -40; cellX <= 40 && clumps < wanted; cellX++)
        for (var cellZ = -40; cellZ <= 40 && clumps < wanted; cellZ++)
        {
            if (!PlantClumps.TryGetClump(spec, WorldGenerationConfig.Seed, cellX, cellZ, out var x, out var z)) continue;
            if (!CottonGenerator.IsCottonClimate(x, z)) continue;
            clumps++;
            for (var dx = -spec.Radius; dx <= spec.Radius; dx++)
            for (var dz = -spec.Radius; dz <= spec.Radius; dz++)
            {
                var wx = x + dx;
                var wz = z + dz;
                var height = ColumnCache.GetHeights(wx >> Chunk.SizeShift, wz >> Chunk.SizeShift)
                    [((wx & Chunk.SizeMask) << Chunk.SizeShift) | (wz & Chunk.SizeMask)];
                var key = (wx >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, wz >> Chunk.SizeShift);
                if (!chunks.ContainsKey(key))
                    chunks[key] = new Chunk(null!, new Vector3(key.Item1, key.Item2, key.Item3), generateBlockData: true);
            }
        }

        Assert.True(clumps >= wanted, $"found only {clumps} cotton clumps of {wanted}");
        return new List<Chunk>(chunks.Values);
    }

    [Fact]
    public void CottonStampsOnlyOnDryGrassInsideAClumpOnItsFringe()
    {
        var cotton = CottonPlant.Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var found = 0;
        foreach (var chunk in ChunksOverClumps(6))
        {
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                if (chunk.GetLocalBlock(x, y, z) != cotton) continue;
                found++;
                var wx = (int)chunk.Position.X * Chunk.Size + x;
                var wz = (int)chunk.Position.Z * Chunk.Size + z;
                var wy = (int)chunk.Position.Y * Chunk.Size + y;
                Assert.True(y > 0);
                Assert.Equal(grass, chunk.GetLocalBlock(x, y - 1, z));
                Assert.False(TerrainGenerator.IsWaterColumn(wx, wz, wy - 1));
                Assert.False(TerrainGenerator.IsBeachHeight(wy - 1));
                Assert.True(CottonGenerator.TryGetCotton(wx, wz), $"cotton at ({wx}, {wy}, {wz}) is not a clump member");
            }
        }

        Assert.True(found > 0, "expected cotton over the sampled clumps");
    }

    [Fact]
    public void CottonClumpsCentreOnForestThatTheSandDitherNeverReaches()
    {
        // The fringe's top end is the start of the grass/sand dither, so the column a clump is
        // centred on always keeps the forest surface (the per-column biome is the dithered one)
        var spec = CottonGenerator.Clumps;
        var gated = 0;
        for (var cellX = -60; cellX <= 60; cellX++)
        for (var cellZ = -60; cellZ <= 60; cellZ++)
        {
            if (!PlantClumps.TryGetClump(spec, WorldGenerationConfig.Seed, cellX, cellZ, out var x, out var z)) continue;
            if (!CottonGenerator.IsCottonClimate(x, z)) continue;
            gated++;
            var surface = ColumnCache.GetBiomes(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
                [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
            Assert.Equal(Biome.Forest, surface);
        }

        Assert.True(gated > 0, "expected cotton clumps in the sampled cells");
    }
}
