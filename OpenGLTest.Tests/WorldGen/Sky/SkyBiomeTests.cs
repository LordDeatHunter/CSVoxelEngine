using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Sky;
using VoxelEngine.WorldGen.Structures;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Sky;

/// <summary>
/// The sky biomes and their flora (plans/LAYERS.md L-13): the biome is a coherent pure function of
/// position and seed and both biomes occur; an island wears its biome's palette from top to core; its
/// plants stand on its plain top, its vines hang from it or the vine over them; its trees root on its top,
/// grow only into open sky inside their own cell and the band, and come out the same whichever chunk
/// stamps them and across a cache reset; and every flora block pops when what holds it goes.
/// </summary>
[Collection("Worldgen seed")]
public sealed class SkyBiomeTests : IDisposable
{
    private static readonly SkyBiome[] AllBiomes = Enum.GetValues<SkyBiome>();

    public SkyBiomeTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>The first <paramref name="count"/> islands of <paramref name="biome"/> a ring scan of island
    /// cells finds around the origin.</summary>
    private static List<SkyIsland> IslandsOf(SkyBiome biome, int count)
    {
        var found = new List<SkyIsland>();
        for (var ring = 0; ring < 160 && found.Count < count; ring++)
        for (var gx = -ring; gx <= ring; gx++)
        for (var gz = -ring; gz <= ring; gz++)
        {
            if (Math.Max(Math.Abs(gx), Math.Abs(gz)) != ring || found.Count >= count) continue;
            if (SkyIslands.TryGetIsland(gx, gz, out var island) && SkyBiomes.OfIsland(island) == biome)
                found.Add(island);
        }

        Assert.True(found.Count == count, $"only {found.Count} {biome} islands found");
        return found;
    }

    /// <summary>Every column of <paramref name="island"/> with its cells.</summary>
    private static IEnumerable<(int X, int Z, int Bottom, int Top)> Columns(SkyIsland island)
    {
        var r = (int)MathF.Ceiling(island.Radius);
        for (var x = (int)island.CenterX - r; x <= (int)island.CenterX + r; x++)
        for (var z = (int)island.CenterZ - r; z <= (int)island.CenterZ + r; z++)
            if (SkyIslandGenerator.TryGetColumn(island, x, z, out var bottom, out var top))
                yield return (x, z, bottom, top);
    }

    /// <summary>The trees rooted in the tree cells over <paramref name="island"/>'s footprint.</summary>
    private static List<(TreeBatches.TreeShape Tree, int Cx, int Cz)> TreesOn(SkyIsland island)
    {
        var trees = new List<(TreeBatches.TreeShape, int, int)>();
        var r = (int)MathF.Ceiling(island.Radius);
        for (var cx = SkyTrees.CellOf((int)island.CenterX - r); cx <= SkyTrees.CellOf((int)island.CenterX + r); cx++)
        for (var cz = SkyTrees.CellOf((int)island.CenterZ - r); cz <= SkyTrees.CellOf((int)island.CenterZ + r); cz++)
            if (SkyTrees.TreeOf(cx, cz) is { } tree)
                trees.Add((tree, cx, cz));
        return trees;
    }

    [Fact]
    public void TheBiomeIsCoherentAndBothBiomesOccur()
    {
        var counts = new int[AllBiomes.Length];
        int pairs = 0, agreeing = 0;
        for (var x = -4096; x < 4096; x += 32)
        for (var z = -4096; z < 4096; z += 32)
        {
            var biome = SkyBiomes.At(x, z);
            Assert.Equal(biome, SkyBiomes.At(x, z));
            counts[(int)biome]++;

            // A neighbor a short island's width away mostly agrees: biomes are regions, not speckle
            pairs++;
            if (SkyBiomes.At(x + 32, z) == biome) agreeing++;
        }

        Assert.All(counts, c => Assert.True(c > counts.Sum() / 5, $"biome counts {string.Join(", ", counts)}"));
        Assert.True(agreeing > pairs * 0.9, $"{agreeing} of {pairs} neighbors agree");
    }

    [Fact]
    public void TheBiomeLayoutFollowsTheWorldSeed()
    {
        static List<SkyBiome> Sample()
        {
            var list = new List<SkyBiome>();
            for (var x = -2048; x < 2048; x += 128)
            for (var z = -2048; z < 2048; z += 128)
                list.Add(SkyBiomes.At(x, z));
            return list;
        }

        var first = Sample();
        WorldSeed.Apply(WorldSeed.Default + 7);
        Assert.NotEqual(first, Sample());
        WorldSeed.Apply(WorldSeed.Default);
        Assert.Equal(first, Sample());
    }

    [Fact]
    public void AnIslandWearsItsBiomesPaletteFromTopToCore()
    {
        foreach (var biome in AllBiomes)
        {
            var palette = SkyBiomes.Palette(biome);
            var sparkles = 0;
            foreach (var island in IslandsOf(biome, 3))
            foreach (var (x, z, bottom, top) in Columns(island))
            {
                var surface = SkyIslandGenerator.BlockAt(x, top, z);
                Assert.True(surface == palette.Top || (surface == palette.Sparkle && palette.Sparkle != BlockType.Air),
                    $"{biome} top at ({x}, {top}, {z}) is {BlockType.ById[surface].Name}");
                if (surface != palette.Top) sparkles++;
                for (var y = bottom; y < top; y++)
                {
                    var block = SkyIslandGenerator.BlockAt(x, y, z);
                    Assert.Equal(y >= top - palette.SoilDepth ? palette.Soil : palette.Core, block);
                }
            }

            Assert.Equal(palette.Sparkle != BlockType.Air, sparkles > 0);
        }
    }

    [Fact]
    public void PlantsStandOnTheirPlainTopAndVinesHangFromTheIslandOrTheVineOverThem()
    {
        foreach (var biome in AllBiomes)
        {
            var palette = SkyBiomes.Palette(biome);
            int plants = 0, vines = 0;
            foreach (var island in IslandsOf(biome, 3))
            foreach (var (x, z, bottom, top) in Columns(island))
            {
                var over = SkyIslandGenerator.BlockAt(x, top + 1, z);
                if (over != BlockType.Air)
                {
                    plants++;
                    Assert.Contains(over, palette.Plants);
                    Assert.Equal(palette.Top, SkyIslandGenerator.BlockAt(x, top, z));
                }

                // Under the island: a run of vine cells straight down from its bottom cell, then open sky
                var y = bottom - 1;
                for (; SkyIslandGenerator.BlockAt(x, y, z) == SkyFlora.VineBlock; y--)
                {
                    vines++;
                    var above = SkyIslandGenerator.BlockAt(x, y + 1, z);
                    Assert.True(above == SkyFlora.VineBlock || BlockType.IsSolid(above));
                }

                Assert.True(bottom - 1 - y <= SkyIslandConfig.SkyVineMaxLength);
                Assert.True(y >= WorldGenerationConfig.SkyIslandBottomY, "a vine hangs out of the band");
                for (var k = 0; k < SkyIslandConfig.SkyVineMaxLength; k++)
                    Assert.Equal(BlockType.Air, SkyIslandGenerator.BlockAt(x, y - k, z));
            }

            Assert.Equal(palette.Plants.Length > 0, plants > 0);
            Assert.Equal(palette.VineRate > 0, vines > 0);
        }
    }

    [Fact]
    public void TreesRootOnTheirIslandsTopAndGrowOnlyIntoOpenSkyInsideTheirCellAndTheBand()
    {
        const int cell = SkyIslandConfig.SkyTreeCellBlocks;
        foreach (var biome in AllBiomes)
        {
            var palette = SkyBiomes.Palette(biome);
            var leaves = 0;
            var trees = IslandsOf(biome, 4).SelectMany(TreesOn).ToList();
            Assert.Equal(palette.Tree != null, trees.Count > 0);

            foreach (var (tree, cx, cz) in trees)
            {
                Assert.True(SkyIslandGenerator.TryGetColumn(tree.X, tree.Z, out var island, out _, out var top));
                Assert.Same(SkyBiomes.Palette(SkyBiomes.OfIsland(island)).Tree, tree.Species);
                Assert.Equal(top + 1, tree.BaseY);
                Assert.Equal(SkyBiomes.Palette(SkyBiomes.OfIsland(island)).Top, SkyIslandGenerator.BlockAt(tree.X, top, tree.Z));
                Assert.True(tree.Cells.Length > 1, "a tree is a lone cell");
                foreach (var c in tree.Cells)
                {
                    Assert.InRange(c.X, cx * cell, cx * cell + cell - 1);
                    Assert.InRange(c.Z, cz * cell, cz * cell + cell - 1);
                    Assert.InRange(c.Y, WorldGenerationConfig.SkyIslandBottomY, WorldGenerationConfig.SkyIslandTopY);
                    Assert.False(BlockType.IsSolid(SkyIslandGenerator.BlockAt(c.X, c.Y, c.Z)),
                        $"a {tree.Species.Name} cell at ({c.X}, {c.Y}, {c.Z}) inside an island");
                    if (c.Block == tree.Species.LeavesId) leaves++;
                }
            }

            Assert.True(palette.Tree == null || leaves > 0, $"no {biome} tree grew a single leaf");
        }
    }

    [Fact]
    public void ATreeIsTheSameWhicheverChunkStampsItAndAcrossACacheReset()
    {
        foreach (var biome in AllBiomes)
        {
            // A tree crossing a chunk seam vertically: its cells come from two chunks
            var (tree, cx, cz) = IslandsOf(biome, 6).SelectMany(TreesOn)
                .First(t => t.Tree.MinY >> Chunk.SizeShift != t.Tree.MaxY >> Chunk.SizeShift);
            var expected = tree.Cells.ToDictionary(c => (c.X, c.Y, c.Z), c => (c.Block, c.State));

            var stamped = new Dictionary<(int, int, int), (byte, byte)>();
            var x0 = tree.X >> Chunk.SizeShift << Chunk.SizeShift;
            var z0 = tree.Z >> Chunk.SizeShift << Chunk.SizeShift;
            for (var cy = tree.MinY >> Chunk.SizeShift; cy <= tree.MaxY >> Chunk.SizeShift; cy++)
            {
                var chunk = new Chunk(null!, new Vector3(x0 >> Chunk.SizeShift, cy, z0 >> Chunk.SizeShift), generateBlockData: false);
                ChunkTerrainFill.GenerateBlockData(chunk);
                var state = chunk.StateArray;
                Assert.NotNull(state);
                foreach (var c in tree.Cells)
                {
                    if (c.Y >> Chunk.SizeShift != cy) continue;
                    var index = Chunk.Idx(c.X - x0, c.Y - cy * Chunk.Size, c.Z - z0);
                    stamped[(c.X, c.Y, c.Z)] = (chunk.Blocks[index], state[index]);
                }
            }

            // A ruin on the island may stand where a tree does, and owns those cells
            foreach (var key in expected.Keys.ToList())
                if (RuinPlacement.SkyRuinOwns(key.Item1, key.Item2, key.Item3))
                {
                    expected.Remove(key);
                    stamped.Remove(key);
                }
            Assert.Equal(expected.OrderBy(e => e.Key), stamped.OrderBy(e => e.Key));

            // A pure function of its cell: regrown from nothing after the cache is dropped, it matches
            WorldSeed.Apply(WorldSeed.Default);
            var regrown = SkyTrees.TreeOf(cx, cz);
            Assert.NotNull(regrown);
            Assert.NotSame(tree, regrown);
            Assert.Equal(tree.Cells, regrown.Cells);
        }
    }

    [Fact]
    public void EveryFloraBlockFallsWhenWhatHoldsItGoes()
    {
        foreach (var biome in AllBiomes)
        foreach (var plant in SkyBiomes.Palette(biome).Plants)
        {
            Assert.False(BlockType.IsSolid(plant));
            Assert.True(BlockType.ById[plant].NeedsSupport, $"{BlockType.ById[plant].Name} floats");
        }

        var vine = BlockType.ById[SkyFlora.VineBlock];
        Assert.False(BlockType.IsSolid(SkyFlora.VineBlock));
        Assert.True(vine.NeedsSupport && vine.SupportAbove, "the sky vine is not held from above");
    }
}
