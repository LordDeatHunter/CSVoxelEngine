using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>
/// The cave biome shape (plans/CAVE_BIOMES.md CB-2): plain and echo cells carve exactly as without a map, a
/// sub-biome only ever opens more air, a mixed map leaves its plain cells alone, and the carve is a pure function
/// of the seed; the worm radius factor is 1 above the sub-band, equals the shape rule of the cave biome at the
/// node center below it, is pure, keeps a widened worm inside the cells a chunk checks, and the worms carve the
/// same after their cache is dropped. In the "Worldgen seed" collection because two facts reseed.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CaveShapeTests
{
    // Synthetic all-stone chunks deep in the sub-band, around two chunk columns
    private static readonly (int Cx, int Cy, int Cz)[] DeepChunks =
    {
        (0, -15, 0), (-1, -15, 0), (3, -16, -2), (-5, -16, 4), (7, -14, 7), (-9, -15, -6),
    };

    public CaveShapeTests() => TestContent.EnsureLoaded();

    private static ushort[] Carved(int cx, int cy, int cz, CaveBiomeMap? map)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var blocks = new ushort[Chunk.BlockCount];
        Array.Fill(blocks, stone);
        NoiseCaves.Carve(blocks, ColumnCache.Get(cx, cz), cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size, map);
        return blocks;
    }

    private static CaveBiomeMap MapOf(Func<int, CaveBiome> biomeOf)
    {
        var map = new CaveBiomeMap();
        for (var i = 0; i < map.Cells.Length; i++) map.Cells[i] = (byte)biomeOf(i);
        map.Recount();
        return map;
    }

    [Fact]
    public void PlainAndEchoCellsCarveExactlyAsWithoutAMap()
    {
        foreach (var biome in new[] { CaveBiome.Plain, CaveBiome.FrozenEcho, CaveBiome.DesertEcho })
        {
            Assert.False(CaveShape.Shapes(biome));
            var map = MapOf(_ => biome);
            foreach (var (cx, cy, cz) in DeepChunks)
                Assert.True(Carved(cx, cy, cz, map).AsSpan().SequenceEqual(Carved(cx, cy, cz, null)),
                    $"a {biome} map changed the carve of chunk ({cx}, {cy}, {cz})");
        }
    }

    [Fact]
    public void ASubBiomeOnlyEverOpensMoreAir()
    {
        foreach (var biome in Enum.GetValues<CaveBiome>().Where(CaveShape.Shapes))
        {
            var map = MapOf(_ => biome);
            var opened = 0;
            foreach (var (cx, cy, cz) in DeepChunks)
            {
                var plain = Carved(cx, cy, cz, null);
                var shaped = Carved(cx, cy, cz, map);
                for (var i = 0; i < plain.Length; i++)
                {
                    if (plain[i] == shaped[i]) continue;

                    Assert.True(plain[i] != BlockType.Air && shaped[i] == BlockType.Air,
                        $"{biome} closed a cell the plain carve opened in chunk ({cx}, {cy}, {cz})");
                    opened++;
                }
            }

            Assert.True(opened > 0, $"{biome} never opened a cell, so its shape does nothing");
        }
    }

    [Fact]
    public void AMixedMapLeavesItsPlainCellsAsTheyWere()
    {
        var map = MapOf(i => (SplitMix64.Mix((ulong)i * 0x9E3779B97F4A7C15UL) % 3) switch
        {
            0 => CaveBiome.Crystal,
            1 => CaveBiome.Fungal,
            _ => CaveBiome.Plain,
        });
        var differs = false;
        foreach (var (cx, cy, cz) in DeepChunks)
        {
            var plain = Carved(cx, cy, cz, null);
            var mixed = Carved(cx, cy, cz, map);
            for (var i = 0; i < plain.Length; i++)
            {
                if (plain[i] == mixed[i]) continue;

                differs = true;
                Assert.True((CaveBiome)map.Cells[i] != CaveBiome.Plain,
                    $"a plain cell changed under a mixed map in chunk ({cx}, {cy}, {cz})");
            }
        }

        Assert.True(differs, "the mixed map changed nothing, so it proves nothing");
    }

    [Fact]
    public void TheShapedCarveIsAPureFunctionOfTheSeed()
    {
        var map = MapOf(i => (CaveBiome)(i % 6));
        var first = DeepChunks.Select(c => Carved(c.Cx, c.Cy, c.Cz, map)).ToList();
        Assert.True(DeepChunks.Select(c => Carved(c.Cx, c.Cy, c.Cz, map)).Zip(first).All(p => p.First.SequenceEqual(p.Second)),
            "carving twice gave different caves");
        try
        {
            WorldSeed.Apply(WorldSeed.Default + 7919);
            Assert.False(DeepChunks.Select(c => Carved(c.Cx, c.Cy, c.Cz, map)).Zip(first).All(p => p.First.SequenceEqual(p.Second)),
                "another seed carved exactly the same caves");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }

        Assert.True(DeepChunks.Select(c => Carved(c.Cx, c.Cy, c.Cz, map)).Zip(first).All(p => p.First.SequenceEqual(p.Second)),
            "the same seed carved different caves after a reseed");
    }

    [Fact]
    public void TheWormRadiusFactorIsOneAboveTheSubBandAndTheShapeRuleBelowIt()
    {
        var state = 0xCB2_0001UL;
        var deep = new List<Vector3>();
        var notOne = 0;
        for (var n = 0; n < 4000; n++)
        {
            var x = (int)(Next(ref state) % 6000) - 3000 + 0.5f;
            var z = (int)(Next(ref state) % 6000) - 3000 + 0.5f;
            var above = new Vector3(x, CaveBiomes.SubBandTopY + 1 + (int)(Next(ref state) % 300), z);
            Assert.Equal(1f, WormCarver.RadiusFactorAt(above));

            var y = WorldGenerationConfig.BarrierTopY - 20 + (int)(Next(ref state) % (ulong)(CaveBiomes.SubBandTopY - WorldGenerationConfig.BarrierTopY + 21));
            var point = new Vector3(x, y + 0.25f, z);
            deep.Add(point);
            int bx = (int)MathF.Floor(x), bz = (int)MathF.Floor(z);
            var column = ColumnCache.Get(VoxelCoords.WorldToChunk(bx), VoxelCoords.WorldToChunk(bz));
            var i = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(bx), VoxelCoords.WorldToLocal(bz));
            var expected = CaveShape.WormRadiusFactor(CaveBiomes.At(bx, y, bz, column.Biomes[i], column.Heights[i]));
            var factor = WormCarver.RadiusFactorAt(point);
            Assert.Equal(expected, factor);
            if (factor != 1f) notOne++;
        }

        Assert.True(notOne > 0, "no deep point widened a worm, so the rule was never checked");

        // Pure: the same answer again, and from many threads at once
        var serial = deep.Select(WormCarver.RadiusFactorAt).ToArray();
        var parallel = new float[deep.Count];
        Parallel.For(0, deep.Count, k => parallel[k] = WormCarver.RadiusFactorAt(deep[k]));
        Assert.Equal(serial, parallel);
    }

    [Fact]
    public void AWidenedWormStaysInsideTheCellsAChunkChecks()
    {
        var maxFactor = Enum.GetValues<CaveBiome>().Max(CaveShape.WormRadiusFactor);
        const float cell = WorldGenerationConfig.CarverCellBlocks;
        Assert.True(WorldGenerationConfig.WormExcursionCap + cell + WorldGenerationConfig.WormMaxRadius * maxFactor + 1f
                    <= (WorldGenerationConfig.WormReachCells + 1) * cell,
            "a widened worm node can reach past the cells a chunk checks");
        Assert.True(WorldGenerationConfig.WormRoomRadius * maxFactor + 1f <= WorldGenerationConfig.WormReachCells * cell,
            "a widened opening chamber can reach past the cells a chunk checks");
    }

    [Fact]
    public void WormsCarveTheSameAfterTheirCacheIsDropped()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        foreach (var (cx, cy, cz) in new[] { (0, -2, 0), (2, -14, -3), (-4, -16, 5), (6, -4, 1) })
        {
            var column = ColumnCache.Get(cx, cz);
            var first = new ushort[Chunk.BlockCount];
            Array.Fill(first, stone);
            WormCarver.Carve(first, column, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);

            WormCarver.ResetCellCache();
            var second = new ushort[Chunk.BlockCount];
            Array.Fill(second, stone);
            WormCarver.Carve(second, column, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
            Assert.True(first.AsSpan().SequenceEqual(second), $"the worms carved chunk ({cx}, {cy}, {cz}) differently");
        }
    }

    private static ulong Next(ref ulong state) => SplitMix64.Mix(state += 0x9E3779B97F4A7C15UL);
}
