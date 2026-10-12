using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// The floor scenery: a fallen log stamps a whole 3-5 cell detached oak log at one height on the
/// forest floor, and a boulder stamps a 2-4 cell blob of stone or cobblestone on the forest or
/// taiga floor, both only into air and clipped at chunk borders.
/// </summary>
public sealed class SceneryTests
{
    public SceneryTests() => TestContent.EnsureLoaded();

    private static Chunk ChunkAt(int x, int y, int z) =>
        new(null!, new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift),
            generateBlockData: true);

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    [Fact]
    public void AFallenLogLiesWholeAndDetachedOnTheForestFloor()
    {
        var log = FallenLogGenerator.LogBlock;
        for (var cellX = -60; cellX <= 60; cellX++)
        for (var cellZ = -60; cellZ <= 60; cellZ++)
        {
            if (!FallenLogGenerator.TryGetLog(cellX, cellZ, out var sx, out var sz, out var length, out var alongX))
                continue;
            if (TerrainGenerator.BiomeAt(sx, sz) != Biome.Forest) continue;
            var height = HeightAt(sx, sz);
            if (TerrainGenerator.IsWaterColumn(sx, sz, height)) continue;
            Assert.InRange(length, WorldGenerationConfig.FallenLogMinLength, WorldGenerationConfig.FallenLogMaxLength);

            var chunks = new Dictionary<(int, int, int), Chunk>();
            for (var i = 0; i < length; i++)
            {
                var x = alongX ? sx + i : sx;
                var z = alongX ? sz : sz + i;
                var key = (x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift);
                if (!chunks.ContainsKey(key))
                    chunks[key] = ChunkAt(x, height + 1, z);
            }

            // Every cell of the log is there, one height, detached wood (scenery, never a tree)
            for (var i = 0; i < length; i++)
            {
                var x = alongX ? sx + i : sx;
                var z = alongX ? sz : sz + i;
                var chunk = chunks[(x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift)];
                var lx = x & Chunk.SizeMask;
                var ly = (height + 1) & Chunk.SizeMask;
                var lz = z & Chunk.SizeMask;
                Assert.Equal(log, chunk.GetLocalBlock(lx, ly, lz));
                Assert.Equal(WoodState.Detached, chunk.GetLocalState(lx, ly, lz));
            }
            return;
        }

        Assert.Fail("no fallen log found near the origin");
    }

    [Fact]
    public void ABoulderIsABlobOfTwoToFourCellsOnTheFloor()
    {
        for (var cellX = -60; cellX <= 60; cellX++)
        for (var cellZ = -60; cellZ <= 60; cellZ++)
        {
            if (!BoulderGenerator.TryGetBoulder(cellX, cellZ, out var cx, out var cz, out var count, out var blockId))
                continue;
            var biome = TerrainGenerator.BiomeAt(cx, cz);
            if (biome != Biome.Forest && biome != Biome.Taiga) continue;
            var height = HeightAt(cx, cz);
            if (TerrainGenerator.IsWaterColumn(cx, cz, height)) continue;
            Assert.InRange(count, WorldGenerationConfig.BoulderMinBlocks, WorldGenerationConfig.BoulderMaxBlocks);
            Assert.Contains(blockId, new[] { BoulderGenerator.StoneBlock, BoulderGenerator.CobblestoneBlock });

            var chunks = new Dictionary<(int, int, int), Chunk>();
            var found = 0;
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var x = cx + dx;
                var z = cz + dz;
                var key = (x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift);
                if (!chunks.TryGetValue(key, out var chunk))
                    chunks[key] = chunk = ChunkAt(x, height + 1, z);
                if (chunk.GetLocalBlock(x & Chunk.SizeMask, (height + 1) & Chunk.SizeMask, z & Chunk.SizeMask) == blockId)
                    found++;
            }

            Assert.InRange(found, count, count);
            return;
        }

        Assert.Fail("no boulder found near the origin");
    }
}
