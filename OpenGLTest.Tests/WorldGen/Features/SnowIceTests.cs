using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

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
    public void TaigaGrowsALeafyTaigaSpecies()
    {
        for (var cx = -48; cx <= 48; cx++)
        for (var cz = -48; cz <= 48; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Taiga)
                continue;

            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wx = cx * Chunk.Size + x;
                var wz = cz * Chunk.Size + z;
                if (TerrainGenerator.BiomeAt(wx, wz) != Biome.Taiga
                    || !TreeGenerator.TryGetTree(wx, wz, out var species, out _))
                    continue;

                Assert.True(species.Worldgen!.GrowsIn(Biome.Taiga), $"{species.Name} does not grow in taiga");
                Assert.True(species.HasLeaves, $"taiga grew the leafless {species.Name}");
                var shape = TreeGenerator.ShapeOf(wx, wz);
                Assert.Same(species, shape.Species);
                Assert.True(shape.WoodCount > 0);
                return;
            }
        }

        Assert.Fail("no tree found in any taiga chunk");
    }

    [Fact]
    public void LodTaigaCanopyWearsItsSpeciesLeaves()
    {
        var columnX = 0;
        var columnZ = 0;
        TreeSpecies? taiga = null;
        for (var cx = -48; cx <= 48 && taiga == null; cx++)
        for (var cz = -48; cz <= 48 && taiga == null; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Taiga)
                continue;
            for (var x = 0; x < Chunk.Size && taiga == null; x++)
            for (var z = 0; z < Chunk.Size && taiga == null; z++)
            {
                var wx = cx * Chunk.Size + x;
                var wz = cz * Chunk.Size + z;
                if (TerrainGenerator.BiomeAt(wx, wz) != Biome.Taiga
                    || !TreeGenerator.TryGetTree(wx, wz, out var species, out _))
                    continue;

                // A leaf cell that is its column's canopy top, so the LOD must wear this species there
                var shape = TreeGenerator.BatchShapeOf(wx, wz);
                for (var i = shape.WoodCount; i < shape.Cells.Length; i++)
                {
                    var cell = shape.Cells[i];
                    if (!TreeGenerator.TryGetCanopy(cell.X, cell.Z, out var top, out var leaf) || top != cell.Y)
                        continue;
                    if (leaf != species.LeavesId)
                        continue;
                    columnX = cell.X;
                    columnZ = cell.Z;
                    taiga = species;
                    break;
                }
            }
        }
        Assert.NotNull(taiga);

        var spec = LodRegion.Levels[0];
        static int DivFloor(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
        var mesh = LodRegion.BuildMeshData(0, DivFloor(columnX, spec.RegionBlocks), DivFloor(columnZ, spec.RegionBlocks));
        var leafTiles = TestContent.WorldTiles(BlockType.ById[taiga!.LeavesId]!);
        Assert.Contains(mesh.Vertices,
            v => (v.PackedColor >> 24) == 253u && leafTiles.Contains(v.Layer));
    }

    [Fact]
    public void LodFrozenSeaSheetShowsSnowCoveredIce()
    {
        Assert.True(TryFindColumn(
            (x, z) => TerrainGenerator.BiomeAt(x, z) == Biome.Ocean
                      && TerrainGenerator.ColdWeight(x, z) > WorldGenerationConfig.FreezeColdThreshold,
            out var iceX, out var iceZ));

        var spec = LodRegion.Levels[0];
        var mesh = LodRegion.BuildMeshData(0,
            (int)Math.Floor(iceX / (float)spec.RegionBlocks),
            (int)Math.Floor(iceZ / (float)spec.RegionBlocks));
        var ice = BlockType.ById[TerrainGenerator.IceBlock]!;
        var snow = BlockType.ById[TerrainGenerator.SnowBlock]!;
        var frozenTiles = new HashSet<ushort>(ice.FaceTiles);
        foreach (var tile in snow.FaceTiles)
            frozenTiles.Add(tile);
        Assert.Contains(mesh.WaterVertices,
            v => (v.PackedColor >> 24) != 254u && frozenTiles.Contains(v.Layer));
    }

    [Fact]
    public void FrozenPlainsGrowOnlyLeaflessSnags()
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
                // Only the dead tree's bare trunk stands here: no species with leaves may root
                if (!TreeGenerator.TryGetTree(cx * Chunk.Size + x, cz * Chunk.Size + z, out var species, out _))
                    continue;
                Assert.False(species.HasLeaves, $"{species.Name} roots in the frozen plains");
            }
        }
    }

    [Fact]
    public void ColdOceanFreezesAndWarmOceanStaysLiquid()
    {
        // Only columns that really hold sea water at sea level: an ocean-biome shore column whose
        // beach stands at or above the waterline has nothing to freeze or keep liquid.
        Assert.True(TryFindColumn(
            (x, z) => IsSeaColumn(x, z)
                      && TerrainGenerator.ColdWeight(x, z) > WorldGenerationConfig.FreezeColdThreshold,
            out var coldX, out var coldZ));
        Assert.Equal(Ice, SurfaceSeaCell(coldX, coldZ));

        if (TryFindColumn(
            (x, z) => IsSeaColumn(x, z)
                      && TerrainGenerator.ColdWeight(x, z) < WorldGenerationConfig.SnowColdThreshold,
            out var warmX, out var warmZ))
            Assert.Equal(TerrainGenerator.SeaBlock, SurfaceSeaCell(warmX, warmZ));
    }

    /// <summary>Whether (x, z) is an ocean column whose floor lies below a sea-level water surface.</summary>
    private static bool IsSeaColumn(int x, int z)
    {
        if (TerrainGenerator.BiomeAt(x, z) != Biome.Ocean) return false;
        if (TerrainGenerator.WaterLevelAt(x, z) != WorldGenerationConfig.SeaLevel) return false;
        var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return TerrainGenerator.IsWaterColumn(x, z,
            column.Bed[VoxelCoords.ColumnIndex(x & Chunk.SizeMask, z & Chunk.SizeMask)]);
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
            if (SnowGenerator.DepthAt(wx, wz, 0) <= 0) continue;

            var column = ColumnCache.Get(cx, cz);
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
        Assert.Equal(0, SnowGenerator.DepthAt(0, 0, 0));
        Assert.True(TryFindColumn(
            (x, z) => TerrainGenerator.ColdWeight(x, z) > 0.9f
                      && !TerrainGenerator.IsWaterColumn(x, z,
                          ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
                              [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)]),
            out var x2, out var z2));
        Assert.True(SnowGenerator.DepthAt(x2, z2, 0) > 0);
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
        var low = SnowShape.GetBoxes(8, 40, 8, 1, snow.StackMax)[0].Max.Y;
        var high = SnowShape.GetBoxes(8, 40, 8, snow.StackMax, snow.StackMax)[0].Max.Y;
        Assert.True(high > low);
        Assert.Equal(1f, high);
    }

    [Fact]
    public void SnowStackCountsPassThroughAndDeriveFromWorldgen()
    {
        var snow = BlockType.ById[Snow]!;
        Assert.Equal(5, SnowShape.CountWithState(0, 40, 0, 5, snow.StackMax));
        Assert.Equal(SnowGenerator.DepthAt(0, 0, 0) is 0 ? 1 : SnowGenerator.DepthAt(0, 0, 0),
            SnowShape.CountWithState(0, 40, 0, 0, snow.StackMax));
    }

    [Fact]
    public void IceIsATranslucentSolid()
    {
        var ice = BlockType.ById[Ice]!;
        Assert.True(ice.Translucent);
        Assert.True(ice.Solid);
        Assert.False(ice.OccludesNeighbors);
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