using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Covers the cold-weather features: the temperature/humidity biomes (taiga and barren frozen),
/// spruce conifers, layered snow (depth, stacking and the partial-height mesh), and the freezing
/// of cold surface water into ice.
/// </summary>
public sealed class SnowIceTests
{
    public SnowIceTests() => TestContent.EnsureLoaded();

    private static byte Snow => ContentRegistry.RequireBlock("snow").Id;
    private static byte Ice => ContentRegistry.RequireBlock("ice").Id;

    private static bool TryFindColumn(Func<int, int, bool> match, out int x, out int z)
    {
        for (var cx = -64; cx <= 64; cx++)
        for (var cz = -64; cz <= 64; cz++)
        {
            var wx = cx * Chunk.Size + 8;
            var wz = cz * Chunk.Size + 8;
            if (!match(wx, wz)) continue;
            x = wx;
            z = wz;
            return true;
        }

        x = 0;
        z = 0;
        return false;
    }

    [Fact]
    public void TaigaAndFrozenBothExistNearOrigin()
    {
        var sawTaiga = false;
        var sawFrozen = false;
        for (var cx = -64; cx <= 64 && !(sawTaiga && sawFrozen); cx++)
        for (var cz = -64; cz <= 64 && !(sawTaiga && sawFrozen); cz++)
        {
            var biome = TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8);
            sawTaiga |= biome == Biome.Taiga;
            sawFrozen |= biome == Biome.Frozen;
        }

        Assert.True(sawTaiga, "no taiga biome within 64 chunks");
        Assert.True(sawFrozen, "no frozen biome within 64 chunks");
    }

    [Fact]
    public void TaigaGrowsSpruceConifers()
    {
        for (var cx = -48; cx <= 48; cx++)
        for (var cz = -48; cz <= 48; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Taiga)
                continue;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                if (!TreeGenerator.TryGetTrunk(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out var shape))
                    continue;

                Assert.Equal(TreeGenerator.SpruceLeavesBlock, shape.LeafBlock);
                Assert.NotEmpty(shape.Wood);
                foreach (var block in shape.Wood)
                    Assert.Equal(BlockType.SpruceWoodFamily, BlockType.WoodFamilyOf(block.Id));
                return;
            }
        }

        Assert.Fail("no spruce found in any taiga chunk");
    }

    [Fact]
    public void FrozenPlainsGrowNoTrees()
    {
        for (var cx = -48; cx <= 48; cx++)
        for (var cz = -48; cz <= 48; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Frozen)
                continue;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                if (TerrainGenerator.BiomeAt(cx * Chunk.Size + x, cz * Chunk.Size + z) != Biome.Frozen)
                    continue;
                Assert.False(TreeGenerator.TryGetTrunk(cx * Chunk.Size + x, cz * Chunk.Size + z, out _, out _));
            }
        }
    }

    [Fact]
    public void ColdOceanFreezesAndWarmOceanStaysLiquid()
    {
        Assert.True(TryFindColumn(
            (x, z) => TerrainGenerator.BiomeAt(x, z) == Biome.Ocean
                      && TerrainGenerator.ColdWeight(x, z) > WorldGenerationConfig.FreezeColdThreshold,
            out var coldX, out var coldZ));
        Assert.Equal(Ice, SurfaceSeaCell(coldX, coldZ));

        if (TryFindColumn(
            (x, z) => TerrainGenerator.BiomeAt(x, z) == Biome.Ocean
                      && TerrainGenerator.ColdWeight(x, z) < WorldGenerationConfig.SnowColdThreshold,
            out var warmX, out var warmZ))
            Assert.Equal(TerrainGenerator.SeaBlock, SurfaceSeaCell(warmX, warmZ));
    }

    /// <summary>The block at sea level in the chunk holding the column (x, z).</summary>
    private static byte SurfaceSeaCell(int x, int z)
    {
        var cy = WorldGenerationConfig.SeaLevel >> Chunk.SizeShift;
        var chunk = new Chunk(null!, new Vector3(x >> Chunk.SizeShift, cy, z >> Chunk.SizeShift),
            generateBlockData: true);
        return chunk.GetLocalBlock(x & Chunk.SizeMask, WorldGenerationConfig.SeaLevel - cy * Chunk.Size,
            z & Chunk.SizeMask);
    }

    [Fact]
    public void SnowSettlesOnColdDryGround()
    {
        // A frozen column with no ice spike: the cell above the surface is a snow layer.
        for (var cx = -48; cx <= 48; cx++)
        for (var cz = -48; cz <= 48; cz++)
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var wx = cx * Chunk.Size + x;
            var wz = cz * Chunk.Size + z;
            if (TerrainGenerator.BiomeAt(wx, wz) != Biome.Frozen) continue;
            if (IceSpikeGenerator.TryGetSpike(wx, wz, out _, out _)) continue;
            if (SnowGenerator.DepthAt(wx, wz) <= 0) continue;

            var column = Chunk.GetColumnData(cx, cz);
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            if (TerrainGenerator.IsWaterColumn(wx, wz, height)) continue;

            var cy = height >> Chunk.SizeShift;
            var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: true);
            Assert.Equal(Snow, chunk.GetLocalBlock(x, height + 1 - cy * Chunk.Size, z));
            return;
        }

        Assert.Fail("no snow-covered frozen column found");
    }

    [Fact]
    public void SnowDepthIsZeroWhenWarmAndPositiveWhenCold()
    {
        Assert.Equal(0, SnowGenerator.DepthAt(0, 0));
        Assert.True(TryFindColumn(
            (x, z) => TerrainGenerator.ColdWeight(x, z) > 0.9f
                      && !TerrainGenerator.IsWaterColumn(x, z,
                          Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
                              [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)]),
            out var x2, out var z2));
        Assert.True(SnowGenerator.DepthAt(x2, z2) > 0);
    }

    [Fact]
    public void SnowLayersWalkThroughUntilFull()
    {
        var snow = BlockType.ById[Snow]!;
        Assert.True(snow.StackMax >= 2);
        Assert.False(BlockType.IsSolidCell(Snow, 1));
        Assert.False(BlockType.IsSolidCell(Snow, (byte)(snow.StackMax - 1)));
        Assert.True(BlockType.IsSolidCell(Snow, snow.StackMax));

        // Every other block keeps its own solidity.
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(BlockType.IsSolidCell(stone, 0));

        // The hitbox rises with the stack.
        var low = SnowShape.GetBoxes(8, 8, 1, snow.StackMax)[0].Max.Y;
        var high = SnowShape.GetBoxes(8, 8, snow.StackMax, snow.StackMax)[0].Max.Y;
        Assert.True(high > low);
        Assert.Equal(1f, high);
    }

    [Fact]
    public void SnowStackCountsPassThroughAndDeriveFromWorldgen()
    {
        var snow = BlockType.ById[Snow]!;
        Assert.Equal(5, SnowShape.CountWithState(0, 0, 5, snow.StackMax));
        Assert.Equal(SnowGenerator.DepthAt(0, 0) is 0 ? 1 : SnowGenerator.DepthAt(0, 0),
            SnowShape.CountWithState(0, 0, 0, snow.StackMax));
    }

    [Fact]
    public void IceIsATranslucentSolid()
    {
        var ice = BlockType.ById[Ice]!;
        Assert.True(ice.Translucent);
        Assert.True(ice.Solid);
        Assert.False(ice.OccludesNeighbours);
    }

    [Fact]
    public void SnowLayerMeshRisesWithState()
    {
        var snow = BlockType.ById[Snow]!;

        float TopOf(byte state)
        {
            var (manager, center) = MeshTestWorld.WalledArea();
            center.SetBlockRaw(8, 8, 8, Snow, state);
            var mesh = center.GenerateMeshData(manager, greedy: false);
            Assert.NotNull(mesh);
            var top = 0f;
            foreach (var v in mesh.Vertices)
                if (v.Position.Y > top) top = v.Position.Y;
            return top;
        }

        Assert.True(TopOf(snow.StackMax) > TopOf(1));
    }
}