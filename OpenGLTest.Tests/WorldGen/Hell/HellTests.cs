using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Hell;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Hell;

/// <summary>
/// Hell's cavern layers (plans/LAYERS.md L-6): layers repeat downward with the configured period,
/// each a pocket that opens somewhere over an unbroken hellrock slab; deeper layers are more open and
/// pond more of their floors; the stamp equals its point query across chunk seams, the fill hands a
/// hell chunk to it whole and never touches a cell above hell; the lava never needs waking; the
/// caverns follow the world seed; and hellrock needs the iron pickaxe.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HellTests
{
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;
    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;

    public HellTests() => TestContent.EnsureLoaded();

    private static byte Id(string name) => ContentRegistry.RequireBlock(name).Id;

    /// <summary>The top row of hell layer <paramref name="layer"/>.</summary>
    private static int LayerTop(int layer) => WorldGenerationConfig.HellTopY - layer * Period;

    /// <summary>A chunk column whose 3x3 ring of chunk columns lies wholly in a biome whose every pool
    /// holds lava, in hell layer <paramref name="layer"/>, so its floors pond lava.</summary>
    private static (int Cx, int Cz) LavaColumn(int layer)
    {
        for (var cx = -200; cx <= 200; cx += 3)
        for (var cz = -200; cz <= 200; cz += 3)
        {
            var allLava = true;
            for (var x = (cx - 1) * Chunk.Size; x <= (cx + 2) * Chunk.Size && allLava; x += Chunk.Size / 2)
            for (var z = (cz - 1) * Chunk.Size; z <= (cz + 2) * Chunk.Size && allLava; z += Chunk.Size / 2)
                allLava = HellBiomes.Palette(HellBiomes.AtLayer(x, layer, z)).LavaShare >= 1f;
            if (allLava) return (cx, cz);
        }

        throw new InvalidOperationException($"no all-lava biome region found in layer {layer}");
    }

    /// <summary>
    /// Open, pool and total cell counts over the pocket rows of a layer, on a grid of point queries
    /// spread over 128 x 128 columns.
    /// </summary>
    private static (int Open, int Pool, int Total) PocketCounts(int layer)
    {
        var top = LayerTop(layer);
        int open = 0, pool = 0, total = 0;
        for (var y = top; y > top - HellGenerator.PocketHeight; y--)
        for (var x = 0; x < 128; x += 2)
        for (var z = 0; z < 128; z += 2)
        {
            total++;
            if (!HellGenerator.IsOpen(x, y, z)) continue;
            open++;
            if (HellGenerator.InPool(y)) pool++;
        }
        return (open, pool, total);
    }

    [Fact]
    public void TheLayersRepeatWithThePeriodEachAPocketOverASolidSlab()
    {
        foreach (var layer in new[] { 0, 1, 2, 7, 1000 })
        {
            var top = LayerTop(layer);
            Assert.Equal(layer, HellGenerator.LayerIndex(top));
            Assert.Equal(layer, HellGenerator.LayerIndex(top - Period + 1));
            Assert.Equal(layer + 1, HellGenerator.LayerIndex(top - Period));

            var open = 0;
            for (var y = top; y > top - Period; y--)
            {
                // Each row is a slab row exactly when the same row of the next layer down is
                Assert.Equal(HellGenerator.InSlab(y), HellGenerator.InSlab(y - Period));
                for (var x = 0; x < 128; x += 3)
                for (var z = 0; z < 128; z += 3)
                {
                    var block = HellGenerator.BlockAt(x, y, z);
                    if (HellGenerator.InSlab(y))
                        Assert.Equal(HellGenerator.HellrockBlock, block);
                    else if (HellGenerator.IsOpen(x, y, z))
                        open++;
                }
            }

            Assert.True(open > 0, $"layer {layer}'s pocket never opens");
        }

        // The slab sits at the bottom of the layer and is the configured thickness
        var slabRows = 0;
        for (var y = LayerTop(3); y > LayerTop(4); y--)
            if (HellGenerator.InSlab(y)) slabRows++;
        Assert.Equal(WorldGenerationConfig.HellSlabThicknessBlocks, slabRows);
        Assert.True(HellGenerator.InSlab(LayerTop(4) + 1));
        Assert.False(HellGenerator.InSlab(LayerTop(3)));
    }

    [Fact]
    public void DeeperLayersAreMoreOpenAndPondMoreOfTheirFloors()
    {
        var shallow = PocketCounts(0);
        var deep = PocketCounts(8);

        double OpenShare((int Open, int Pool, int Total) c) => (double)c.Open / c.Total;
        double PoolShare((int Open, int Pool, int Total) c) => (double)c.Pool / c.Total;

        Assert.True(OpenShare(deep) > OpenShare(shallow),
            $"open share {OpenShare(deep):F3} deep vs {OpenShare(shallow):F3} at the top");
        Assert.True(PoolShare(deep) > PoolShare(shallow),
            $"pool share {PoolShare(deep):F3} deep vs {PoolShare(shallow):F3} at the top");
        Assert.True(shallow.Open > shallow.Pool && deep.Open > deep.Pool, "a pocket holds no air to stand in");
    }

    [Fact]
    public void TheStampEqualsThePointQueryAcrossChunkSeamsAndLeavesTheCellsAboveHell()
    {
        var stone = Id("stone");
        var blocks = new byte[Volume];
        var seen = new HashSet<byte>();
        var sawAbove = false;

        // The chunk straddling hell's top, a pocket chunk and a deep layer's chunk, over 3x3 columns
        int[] floors =
        [
            WorldGenerationConfig.HellTopY >> Chunk.SizeShift << Chunk.SizeShift,
            (LayerTop(0) - 60) >> Chunk.SizeShift << Chunk.SizeShift,
            (LayerTop(9) - 70) >> Chunk.SizeShift << Chunk.SizeShift,
        ];
        var (lavaCx, lavaCz) = LavaColumn(9);
        foreach (var y0 in floors)
        for (var x0 = (lavaCx - 1) * Chunk.Size; x0 <= (lavaCx + 1) * Chunk.Size; x0 += Chunk.Size)
        for (var z0 = (lavaCz - 1) * Chunk.Size; z0 <= (lavaCz + 1) * Chunk.Size; z0 += Chunk.Size)
        {
            Array.Fill(blocks, stone);
            HellGenerator.Stamp(blocks, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wy = y0 + y;
                var got = blocks[Chunk.Idx(x, y, z)];
                if (!HellGenerator.InHell(wy))
                {
                    Assert.Equal(stone, got);
                    sawAbove = true;
                    continue;
                }

                Assert.Equal(HellGenerator.BlockAt(x0 + x, wy, z0 + z), got);
                seen.Add(got);
            }
        }

        Assert.True(sawAbove, "no sampled chunk reaches above hell");
        Assert.Contains(BlockType.Air, seen);
        Assert.Contains(HellGenerator.LavaBlock, seen);
        Assert.Contains(HellGenerator.HellrockBlock, seen);
        Assert.Throws<ArgumentOutOfRangeException>(() => HellGenerator.BlockAt(0, WorldGenerationConfig.HellTopY + 1, 0));
        Assert.Equal(-1, HellGenerator.LayerIndex(WorldGenerationConfig.HellTopY + 1));
    }

    [Fact]
    public void TheFillHandsHellCellsToTheGeneratorAndItsLavaNeverNeedsWaking()
    {
        var sawLava = false;
        var straddling = WorldGenerationConfig.HellTopY >> Chunk.SizeShift;
        int[] cys = [straddling, straddling - 2, (LayerTop(5) - 66) >> Chunk.SizeShift];
        var (lavaCx, lavaCz) = LavaColumn(5);
        foreach (var cy in cys)
        for (var cx = lavaCx - 1; cx <= lavaCx + 1; cx++)
        for (var cz = lavaCz - 1; cz <= lavaCz + 1; cz++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wy = chunk.LowerBoundY + y;
                if (!HellGenerator.InHell(wy)) continue;
                var block = chunk.Blocks[Chunk.Idx(x, y, z)];
                Assert.Equal(HellGenerator.BlockAt(cx * Chunk.Size + x, wy, cz * Chunk.Size + z), block);
                sawLava |= block == HellGenerator.LavaBlock;
            }

            // A lava cell's sides and bottom are lava or hellrock, so nothing in it can flow
            if (HellGenerator.Covers(chunk.LowerBoundY))
                Assert.Null(chunk.FluidFrontier);
        }

        Assert.True(sawLava, "no generated hell chunk holds lava, so the frontier rule was never tested");
    }

    [Fact]
    public void TheCavernsFollowTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = OpenCells();
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.False(first.SetEquals(OpenCells()));
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(first.SetEquals(OpenCells()));
            Assert.NotEmpty(first);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static HashSet<(int, int, int)> OpenCells()
    {
        var set = new HashSet<(int, int, int)>();
        var mid = LayerTop(1) - HellGenerator.PocketHeight / 2;
        for (var x = 0; x < 48; x += 2)
        for (var z = 0; z < 48; z += 2)
        for (var y = mid - 8; y <= mid + 8; y++)
            if (HellGenerator.IsOpen(x, y, z)) set.Add((x, y, z));
        return set;
    }

    [Fact]
    public void OnlyTheIronPickaxeMinesHellrock()
    {
        var hellrock = HellGenerator.HellrockBlock;
        Assert.True(Mining.CanMine(hellrock, ContentRegistry.RequireItem("iron_pickaxe")));
        Assert.False(Mining.CanMine(hellrock, ContentRegistry.RequireItem("bronze_pickaxe")));
        Assert.False(Mining.CanMine(hellrock, null));
    }
}
