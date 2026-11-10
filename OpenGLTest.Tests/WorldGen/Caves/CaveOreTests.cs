using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Loot;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using Xunit;
using Stage = VoxelEngine.WorldGen.WorldgenStageTimer.Stage;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>
/// The cave biome exclusive ores (plans/CAVE_BIOMES.md CB-2): the stamp equals the point query across seams
/// and its highest y, an ore grows only in cells of its own biome and never above its band, a generated chunk
/// holds exactly the ore the point query says for each stone cell's biome, each ore block is its own
/// self-dropping, pickaxe-gated block, and the same seed grows the same ores. In the "Worldgen seed"
/// collection because it reads the worldgen noises and one fact reseeds.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CaveOreTests
{
    private static readonly CaveBiome[] OreBiomes = Enum.GetValues<CaveBiome>().Where(b => CaveOres.OreOf(b) != 0).ToArray();
    private static readonly int[] ChunkXz = { -1, 0 };

    public CaveOreTests() => TestContent.EnsureLoaded();

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;

    /// <summary>The chunk floors around an ore's highest y: one chunk below, the one holding it, one above.</summary>
    private static IEnumerable<(int X0, int Y0, int Z0)> StraddlingChunks(CaveBiome biome)
    {
        var top = CaveOres.MaxYOf(biome) >> Chunk.SizeShift;
        foreach (var cx in ChunkXz)
        foreach (var cy in new[] { top - 1, top, top + 1 })
        foreach (var cz in ChunkXz)
            yield return (cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
    }

    private static CaveBiomeMap MapOf(Func<int, CaveBiome> biomeOf)
    {
        var map = new CaveBiomeMap();
        for (var i = 0; i < map.Cells.Length; i++) map.Cells[i] = (byte)biomeOf(i);
        map.Recount();
        return map;
    }

    private static ushort[] StampedStone(CaveBiomeMap map, int x0, int y0, int z0)
    {
        var blocks = new ushort[Chunk.BlockCount];
        Array.Fill(blocks, Stone);
        CaveOres.Stamp(blocks, map, x0, y0, z0);
        return blocks;
    }

    [Fact]
    public void TheStampEqualsThePointQueryAndNeverReachesAboveTheOresHighestY()
    {
        foreach (var biome in OreBiomes)
        {
            var map = MapOf(_ => biome);
            var ore = CaveOres.OreOf(biome);
            var oreCells = 0;
            foreach (var (x0, y0, z0) in StraddlingChunks(biome))
            {
                var blocks = StampedStone(map, x0, y0, z0);
                for (var x = 0; x < Chunk.Size; x++)
                for (var y = 0; y < Chunk.Size; y++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    var cell = blocks[Chunk.Idx(x, y, z)];
                    var expected = CaveOres.HasOre(x0 + x, y0 + y, z0 + z, biome) ? ore : Stone;
                    Assert.True(cell == expected, $"{biome}: cell ({x0 + x}, {y0 + y}, {z0 + z}) is {cell}, the query says {expected}");
                    if (cell != ore) continue;

                    oreCells++;
                    Assert.True(y0 + y <= CaveOres.MaxYOf(biome), $"{biome}: ore above its highest y at {y0 + y}");
                }
            }

            Assert.True(oreCells > 0, $"no {biome} ore at all, so the comparison proves nothing");
        }
    }

    [Fact]
    public void AnOreGrowsOnlyInCellsOfItsOwnBiome()
    {
        foreach (var biome in OreBiomes)
        {
            var (x0, y0, z0) = StraddlingChunks(biome).First();
            foreach (var other in Enum.GetValues<CaveBiome>().Where(b => b != biome))
            {
                // A map holding only another biome: the other biome's own ore may grow, this one's never
                var blocks = StampedStone(MapOf(_ => other), x0, y0, z0);
                Assert.DoesNotContain(CaveOres.OreOf(biome), blocks);
            }

            // Half the cells this biome (by x), half plain: ore only in this biome's half, as the query says there
            var mixed = MapOf(i => ((i >> (2 * Chunk.SizeShift)) & Chunk.SizeMask) < Chunk.Size / 2 ? biome : CaveBiome.Plain);
            var ore = CaveOres.OreOf(biome);
            var mixedBlocks = StampedStone(mixed, x0, y0, z0);
            var oreCells = 0;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var idx = Chunk.Idx(x, y, z);
                var cellBiome = (CaveBiome)mixed.Cells[idx];
                var expected = CaveOres.HasOre(x0 + x, y0 + y, z0 + z, cellBiome) ? CaveOres.OreOf(cellBiome) : Stone;
                Assert.True(mixedBlocks[idx] == expected, $"{biome}: mixed map cell ({x}, {y}, {z}) disagrees with the query");
                if (mixedBlocks[idx] == ore) oreCells++;
            }

            Assert.True(oreCells > 0, $"no {biome} ore in the mixed map's half, so the rule proves nothing");
        }
    }

    [Fact]
    public void InRealChunksEveryStoneCellBecomesTheOreItsBiomeSays()
    {
        ushort[]? before = null;
        ushort[]? after = null;
        Chunk? current = null;
        var timer = new WorldgenStageTimer
        {
            AfterMark = s =>
            {
                if (s == Stage.Tin) before = (ushort[])current!.Blocks.Clone();
                else if (s == Stage.CaveOres) after = (ushort[])current!.Blocks.Clone();
            },
        };

        var stone = Stone;
        var exclusive = new HashSet<ushort>(OreBiomes.Select(CaveOres.OreOf));
        var oreCells = 0;
        var lowest = (WorldGenerationConfig.BarrierTopY >> Chunk.SizeShift) + 1;
        var highest = CaveBiomes.SubBandTopY >> Chunk.SizeShift;
        var columns = new[] { (-2, -4), (3, 1), (-7, 6), (11, -9), (-15, -13), (20, 17) };
        foreach (var (cx, cz) in columns)
        {
            var column = ColumnCache.Get(cx, cz);
            for (var cy = lowest; cy <= highest; cy++)
            {
                before = after = null;
                current = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
                ChunkTerrainFill.GenerateBlockData(current, timer);
                Assert.NotNull(before);
                Assert.NotNull(after);

                for (var x = 0; x < Chunk.Size; x++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    var i = (x << Chunk.SizeShift) | z;
                    for (var y = 0; y < Chunk.Size; y++)
                    {
                        var idx = Chunk.Idx(x, y, z);
                        if (before[idx] != stone)
                        {
                            Assert.True(before[idx] == after[idx], $"the ores rewrote a non-stone cell in chunk ({cx}, {cy}, {cz})");
                            continue;
                        }

                        var (wx, wy, wz) = (cx * Chunk.Size + x, cy * Chunk.Size + y, cz * Chunk.Size + z);
                        var biome = CaveBiomes.At(wx, wy, wz, column.Biomes[i], column.Heights[i]);
                        var expected = CaveOres.HasOre(wx, wy, wz, biome) ? CaveOres.OreOf(biome) : stone;
                        Assert.True(after[idx] == expected, $"stone cell ({wx}, {wy}, {wz}) of {biome} became {after[idx]}, expected {expected}");
                        if (exclusive.Contains(after[idx])) oreCells++;
                    }
                }
            }

            if (oreCells > 0) break;
        }

        Assert.True(oreCells > 0, "no exclusive ore in any searched real chunk, so the rule proves nothing");
    }

    [Fact]
    public void EachOreIsItsOwnSelfDroppingPickaxeGatedBlock()
    {
        var stoneTile = ContentRegistry.RequireBlock("stone").FaceTiles[0];
        var tiles = new HashSet<ushort>();
        foreach (var biome in OreBiomes)
        {
            var type = BlockType.ById[CaveOres.OreOf(biome)];
            Assert.True(BlockType.IsSolid(type.Id), type.Name);
            Assert.True(type.RequiresTool, type.Name);
            Assert.True(type.Tools!.ContainsKey("pickaxe"), type.Name);
            Assert.False(Mining.CanMine(type.Id, null), type.Name);
            Assert.IsType<BlockItem>(ItemRegistry.TryGet(type.Id));

            Assert.True(type.FaceTiles.All(tile => tile != stoneTile), $"{type.Name} wears stone's tile");
            Assert.True(tiles.Add(type.FaceTiles[0]), $"{type.Name} shares a tile with another cave ore");

            var rolled = new List<ushort>();
            DropManager.RollLoot(type.Id, new LootContext(0, -240, 0), rolled);
            Assert.Equal(new[] { type.Id }, rolled);
        }
    }

    [Fact]
    public void TheSameSeedGrowsTheSameOres()
    {
        var stamps = OreBiomes.SelectMany(b => StraddlingChunks(b).Take(2).Select(c => (Biome: b, c.X0, c.Y0, c.Z0))).ToArray();
        ushort[][] Stamp() => stamps.Select(s => StampedStone(MapOf(_ => s.Biome), s.X0, s.Y0, s.Z0)).ToArray();

        var first = Stamp();
        try
        {
            WorldSeed.Apply(WorldSeed.Default + 7919);
            Assert.False(Stamp().Zip(first).All(p => p.First.SequenceEqual(p.Second)), "another seed grew exactly the same ores");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }

        Assert.True(Stamp().Zip(first).All(p => p.First.SequenceEqual(p.Second)), "the same seed grew different ores after a reseed");
    }
}
