using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WaterRavineTests
{
    public WaterRavineTests() => TestContent.EnsureLoaded();

    private static byte Water() => ContentRegistry.RequireBlock("water").Id;

    [Fact]
    public void ARavineOnDryLandHoldsNoWater()
    {
        var checkedColumn = false;
        for (var x = -4096; x < 4096 && !checkedColumn; x += 4)
        for (var z = -4096; z < 4096 && !checkedColumn; z += 4)
        {
            var cx = x >> Chunk.SizeShift;
            var cz = z >> Chunk.SizeShift;
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var column = Chunk.GetColumnData(cx, cz);
            if (column.Heights[local] >= WorldGenerationConfig.SeaLevel) continue;
            if (column.Bed[local] < WorldGenerationConfig.SeaLevel) continue;

            Assert.False(TerrainGenerator.IsWaterColumn(x, z, column.Bed[local]));

            var water = Water();
            for (var cy = column.Heights[local] >> Chunk.SizeShift;
                 cy <= WorldGenerationConfig.SeaLevel >> Chunk.SizeShift; cy++)
            {
                var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: true);
                var low = Math.Max(column.Heights[local] + 1, cy * Chunk.Size);
                var high = Math.Min(WorldGenerationConfig.SeaLevel, cy * Chunk.Size + Chunk.SizeMask);
                for (var wy = low; wy <= high; wy++)
                    Assert.NotEqual(water,
                        chunk.GetLocalBlock(x & Chunk.SizeMask, wy & Chunk.SizeMask, z & Chunk.SizeMask));
            }

            checkedColumn = true;
        }

        Assert.True(checkedColumn, "no dry-land ravine below sea level found in the scanned area");
    }

    [Fact]
    public void ARavineUnderAWaterBodyStaysWetAndFills()
    {
        var checkedColumn = false;
        for (var x = 0; x < 2048 && !checkedColumn; x += 4)
        for (var z = 0; z < 2048 && !checkedColumn; z += 4)
        {
            var cx = x >> Chunk.SizeShift;
            var cz = z >> Chunk.SizeShift;
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var column = Chunk.GetColumnData(cx, cz);
            if (column.Bed[local] >= WorldGenerationConfig.SeaLevel) continue;
            if (column.Heights[local] >= column.Bed[local]) continue;
            // A cold column's surface water freezes to ice; only open water proves the fill.
            if (TerrainGenerator.ColdWeight(x, z) > WorldGenerationConfig.FreezeColdThreshold) continue;

            Assert.True(TerrainGenerator.IsWaterColumn(x, z, column.Bed[local]));

            var chunk = new Chunk(null!,
                new Vector3(cx, WorldGenerationConfig.SeaLevel >> Chunk.SizeShift, cz),
                generateBlockData: true);
            Assert.Equal(Water(),
                chunk.GetLocalBlock(x & Chunk.SizeMask, WorldGenerationConfig.SeaLevel & Chunk.SizeMask,
                    z & Chunk.SizeMask));

            checkedColumn = true;
        }

        Assert.True(checkedColumn, "no ravine under a water body found near the origin");
    }

    [Fact]
    public void RavinesFadeOutBeforeTheyReachTheOcean()
    {
        var oceanColumns = 0;
        for (var x = -512; x <= 512; x += 4)
        for (var z = -512; z <= 512; z += 4)
        {
            TerrainGenerator.BiomeWeights(x, z, out var oceanWeight, out _);
            if (oceanWeight < 0.5f) continue;

            var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            Assert.Equal(column.Bed[local], column.Heights[local]);
            oceanColumns++;
        }

        Assert.True(oceanColumns > 0, "no ocean columns found in the scanned area");
    }

    [Fact]
    public void ARavineNeverCutsABankBelowItsWaterBodysLevel()
    {
        var leaks = 0;
        var firstLeak = "";
        for (var x = -1024; x < 1024; x += 2)
        for (var z = -1024; z < 1024; z += 2)
        {
            var column = Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var level = column.WaterLevel[local];
            if (level == TerrainGenerator.NoWater) continue;
            if (column.Biomes[local] == Biome.Ocean) continue;
            if (column.Bed[local] < level) continue;
            if (column.Heights[local] >= level) continue;

            leaks++;
            if (leaks == 1)
                firstLeak = $"({x},{z}) bed={column.Bed[local]} h={column.Heights[local]} level={level}";
        }

        Assert.True(leaks == 0,
            $"{leaks} pond or river banks are cut below their own water level; first {firstLeak}");
    }

    [Fact]
    public void WakeOnlyEntersCarvedCavities()
    {
        var air = BlockType.Air;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var top = 4;

        Assert.False(FluidManager.IsCarvedFlowTarget(air, top + 1, top));
        Assert.True(FluidManager.IsCarvedFlowTarget(air, top, top));
        Assert.True(FluidManager.IsCarvedFlowTarget(air, 0, top));
        Assert.False(FluidManager.IsCarvedFlowTarget(stone, 0, top));
    }

    [Fact]
    public void GeneratedWaterDoesNotWakeIntoOpenAirAboveATerrainTop()
    {
        var found = false;
        var cx = 0;
        var cz = 0;
        var z = 0;
        var top = 0;
        for (var ax = 0; ax <= 16 && !found; ax++)
        for (var az = 0; az <= 16 && !found; az++)
        {
            var a = Chunk.GetColumnData(ax, az);
            var b = Chunk.GetColumnData(ax + 1, az);
            // Both chunks dry and above the waterline: no generated water can interfere.
            if (a.MinHeight < 1 || b.MinHeight < 1) continue;

            for (var t = 0; t < Chunk.Size && !found; t++)
            {
                var h = b.Heights[t];
                if (h < 1 || h > Chunk.Size - 2) continue;
                cx = ax;
                cz = az;
                z = t;
                top = h;
                found = true;
            }
        }
        Assert.True(found, "no above-waterline chunk pair with a low terrain top found");

        Assert.NotEqual(Water(), WakeAcrossBorder(cx, cz, z, top + 1, targetCarved: false));
        Assert.Equal(Water(), WakeAcrossBorder(cx, cz, z, top, targetCarved: true));
    }

    /// <summary>
    /// Queues two horizontally adjacent dry chunks, puts a water source on the last column of
    /// the left one, and settles one tick. Returns the block id that ends up on the first column
    /// of the right chunk, at the same Y and Z, which is where the water can only arrive through
    /// the wake-up path.
    /// </summary>
    private static byte WakeAcrossBorder(int cx, int cz, int z, int worldY, bool targetCarved)
    {
        var manager = new ChunkManager(null!);
        var left = new Vector3(cx, 0, cz);
        var right = new Vector3(cx + 1, 0, cz);
        manager.QueueChunk(left);
        manager.QueueChunk(right);

        var ly = worldY & Chunk.SizeMask;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        manager.Chunks[left].SetBlockRaw(Chunk.SizeMask, ly - 1, z, stone, 0);
        manager.Chunks[left].SetBlockRaw(Chunk.SizeMask, ly, z, Water(), FluidState.Source);
        if (targetCarved) manager.Chunks[right].SetBlockRaw(0, ly, z, 0, 0);

        var fluids = new FluidManager();
        var drops = new DropManager();
        fluids.WakeLoadedChunks(manager);
        fluids.Update(1f, manager, drops);

        var targetX = (cx + 1) * Chunk.Size;
        Assert.True(manager.TryGetCell(targetX, worldY, cz * Chunk.Size + z, out var id, out _));
        return id;
    }
}
