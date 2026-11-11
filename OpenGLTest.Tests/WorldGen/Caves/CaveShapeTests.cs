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
    public void MoreCoverOpensAdditionalRoomsWithoutClosingShallowAir()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        ColumnData Column(int height) => new(Enumerable.Repeat(height, Chunk.Size * Chunk.Size).ToArray(),
            new byte[Chunk.Size * Chunk.Size], new float[Chunk.Size * Chunk.Size], new float[Chunk.Size * Chunk.Size]);
        var shallowColumn = Column(28);
        var deepColumn = Column(340);
        var extra = 0;
        var shallowAir = 0;
        var total = 0;
        for (var cx = -4; cx <= 4; cx++)
        for (var cz = -4; cz <= 4; cz++)
        {
            var shallow = Enumerable.Repeat(stone, Chunk.BlockCount).ToArray();
            var deep = (ushort[])shallow.Clone();
            NoiseCaves.Carve(shallow, shallowColumn, cx * Chunk.Size, 0, cz * Chunk.Size);
            NoiseCaves.Carve(deep, deepColumn, cx * Chunk.Size, 0, cz * Chunk.Size);
            for (var i = 0; i < shallow.Length; i++)
            {
                total++;
                if (shallow[i] == BlockType.Air)
                {
                    shallowAir++;
                    Assert.Equal(BlockType.Air, deep[i]);
                }
                else if (deep[i] == BlockType.Air) extra++;
            }
        }
        Assert.True(shallowAir > 0 && extra > 0);
        Assert.True(shallowAir + extra < total * 0.7, "deep caves became one continuous open region");
        Assert.True(NoiseCaves.PocketThreshold(20) > NoiseCaves.PocketThreshold(200));
    }

    [Fact]
    public void RegularPassagesProvideStandingAndSidewaysClearance()
    {
        var air = 0;
        var standing = 0;
        var wide = 0;
        for (var cx = -4; cx <= 4; cx++)
        for (var cz = -4; cz <= 4; cz++)
        {
            var blocks = Carved(cx, -5, cz, null);
            for (var x = 1; x < Chunk.Size - 1; x++)
            for (var y = 1; y < Chunk.Size - 2; y++)
            for (var z = 1; z < Chunk.Size - 1; z++)
            {
                if (blocks[Chunk.Idx(x, y, z)] != BlockType.Air) continue;
                air++;
                if (blocks[Chunk.Idx(x, y + 1, z)] != BlockType.Air) continue;
                standing++;
                if (blocks[Chunk.Idx(x + 1, y, z)] == BlockType.Air
                    && blocks[Chunk.Idx(x + 1, y + 1, z)] == BlockType.Air
                    && blocks[Chunk.Idx(x, y, z + 1)] == BlockType.Air
                    && blocks[Chunk.Idx(x, y + 1, z + 1)] == BlockType.Air) wide++;
            }
        }
        Assert.True(air > 0);
        Assert.True(standing > air * 0.65, $"standing clearance {standing}/{air}");
        Assert.True(wide > air * 0.35, $"turning clearance {wide}/{air}");
    }

    [Fact]
    public void PlainAndEchoCellsCarveExactlyAsWithoutAMap()
    {
        foreach (var biome in new[] { CaveBiome.Plain, CaveBiome.FrozenEcho })
        {
            Assert.False(CaveShape.Shapes(biome));
            var map = MapOf(_ => biome);
            foreach (var (cx, cy, cz) in DeepChunks)
                Assert.True(Carved(cx, cy, cz, map).AsSpan().SequenceEqual(Carved(cx, cy, cz, null)),
                    $"a {biome} map changed the carve of chunk ({cx}, {cy}, {cz})");
        }
    }

    [Fact]
    public void DesertUsesDistinctCarvingAndLargePocketsKeepSeparatingWalls()
    {
        var map = MapOf(_ => CaveBiome.DesertEcho);
        Assert.Contains(DeepChunks, c => !Carved(c.Cx, c.Cy, c.Cz, map).SequenceEqual(Carved(c.Cx, c.Cy, c.Cz, null)));
        var open = 0;
        var closed = 0;
        for (var x = -192; x < 192; x += 3)
        for (var y = -192; y < -64; y += 2)
        for (var z = -192; z < 192; z += 3)
        {
            if (DesertCaves.InPocket(x, y, z, 340, 1f)) open++;
            else closed++;
            // Pocket boundaries always preserve walls, including at negative world coordinates.
            Assert.False(DesertCaves.InPocket(x, y, 0, 340, 1f));
            Assert.False(DesertCaves.InPocket(0, y, z, 340, 1f));
            Assert.False(DesertCaves.InPocket(x, -128, z, 340, 1f));
        }
        Assert.True(open > 0 && closed > open, "desert rooms must be separate pockets in solid rock");
    }

    [Fact]
    public void DesertPocketOutlinesAreAsymmetricAndVaryBetweenSites()
    {
        var profiles = new HashSet<string>();
        var irregular = 0;
        var tested = 0;
        var cell = WorldGenerationConfig.DesertPocketCellBlocks;
        for (var cx = -3; cx <= 3; cx++)
        for (var cz = -3; cz <= 3; cz++)
        {
            var open = new HashSet<(int X, int Z)>();
            var y = -WorldGenerationConfig.DesertPocketCellHeight * 2 + WorldGenerationConfig.DesertPocketCellHeight / 2;
            for (var x = cx * cell; x < (cx + 1) * cell; x++)
            for (var z = cz * cell; z < (cz + 1) * cell; z++)
                if (DesertCaves.InPocket(x, y, z, 340, 1)) open.Add((x, z));
            if (open.Count == 0) continue;
            tested++;
            var minX = open.Min(p => p.X);
            var maxX = open.Max(p => p.X);
            var minZ = open.Min(p => p.Z);
            var maxZ = open.Max(p => p.Z);
            // An ellipsoid, even with different radii, is symmetric about its bounding box center.
            var unmatched = open.Count(p => !open.Contains((minX + maxX - p.X, minZ + maxZ - p.Z)));
            if (unmatched > open.Count / 10) irregular++;
            var profile = new System.Text.StringBuilder();
            for (var x = 0; x <= 12; x++)
            for (var z = 0; z <= 12; z++)
                profile.Append(open.Contains((minX + x * (maxX - minX) / 12, minZ + z * (maxZ - minZ) / 12)) ? '1' : '0');
            profiles.Add(profile.ToString());

            // A pocket remains a coherent room rather than a scattering of noise holes.
            var reached = new HashSet<(int X, int Z)> { open.First() };
            var pending = new Queue<(int X, int Z)>(reached);
            while (pending.TryDequeue(out var p))
            foreach (var q in new[] { (p.X - 1, p.Z), (p.X + 1, p.Z), (p.X, p.Z - 1), (p.X, p.Z + 1) })
                if (open.Contains(q) && reached.Add(q)) pending.Enqueue(q);
            Assert.Equal(open.Count, reached.Count);
        }
        Assert.True(tested > 0);
        Assert.True(irregular > tested / 2, "most pocket outlines still have ellipsoid symmetry");
        Assert.True(profiles.Count > tested / 2, "sites reuse the same normalized outline");
    }

    [Fact]
    public void DesertPocketOwnershipSurvivesNegativeChunkSeamsQueryOrderAndReseeding()
    {
        var positions = (from x in Enumerable.Range(-8, 16)
                         from z in Enumerable.Range(-8, 16)
                         select (X: x * Chunk.Size, Y: -128, Z: z * Chunk.Size)).ToArray();
        bool[] Sample((int X, int Y, int Z) p) =>
            (from x in Enumerable.Range(0, Chunk.Size)
             from y in Enumerable.Range(0, Chunk.Size)
             from z in Enumerable.Range(0, Chunk.Size)
             select DesertCaves.InPocket(p.X + x, p.Y + y, p.Z + z, 340, 1f)).ToArray();
        var baseline = positions.Select(Sample).ToArray();
        Assert.Contains(baseline, cells => cells.Any(open => open));
        Parallel.For(0, positions.Length, i => Assert.Equal(baseline[i], Sample(positions[i])));
        foreach (var i in Enumerable.Range(0, positions.Length).Reverse()) Assert.Equal(baseline[i], Sample(positions[i]));
        try
        {
            WorldSeed.Apply(WorldSeed.Default + 7919);
            Assert.Contains(Enumerable.Range(0, positions.Length), i => !baseline[i].SequenceEqual(Sample(positions[i])));
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
        foreach (var i in Enumerable.Range(0, positions.Length)) Assert.Equal(baseline[i], Sample(positions[i]));

        // Whole ownership planes, including both sides of zero and negative cells, remain rock.
        var wall = WorldGenerationConfig.DesertPocketWallBlocks;
        var cell = WorldGenerationConfig.DesertPocketCellBlocks;
        for (var boundary = -cell; boundary <= cell; boundary += cell)
        for (var offset = -wall; offset < wall; offset++)
        for (var x = -cell; x < cell; x += 3)
        for (var y = -128; y < -64; y += 3)
        {
            Assert.False(DesertCaves.InPocket(boundary + offset, y, x, 340, 1));
            Assert.False(DesertCaves.InPocket(x, y, boundary + offset, 340, 1));
        }
        // Every open pocket cell is actually carved through the bulk path in adjacent chunks.
        var map = MapOf(_ => CaveBiome.DesertEcho);
        foreach (var p in positions)
        {
            var blocks = Enumerable.Repeat(TerrainGenerator.RockBlock, Chunk.BlockCount).ToArray();
            var heights = Enumerable.Repeat(p.Y + 340, Chunk.Size * Chunk.Size).ToArray();
            var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length], new float[heights.Length]);
            NoiseCaves.Carve(blocks, column, p.X, p.Y, p.Z, map);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                if (DesertCaves.InPocket(p.X + x, p.Y + y, p.Z + z, heights[0] - p.Y - y, 1))
                    Assert.Equal(BlockType.Air, blocks[Chunk.Idx(x, y, z)]);
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
