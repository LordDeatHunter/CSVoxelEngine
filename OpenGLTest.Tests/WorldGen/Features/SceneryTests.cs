using System;
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

    /// <summary>
    /// Whether the layer one above <paramref name="height"/> was free for a scenery stamp at each
    /// of the <paramref name="count"/> columns <paramref name="cell"/> names: the ground there does
    /// not rise into it, and no tree (a leaf, or wood that is not detached scenery) holds the cell.
    /// </summary>
    private static bool LayerIsClearOfGroundAndTrees(Dictionary<(int, int, int), Chunk> chunks, int height,
        int count, Func<int, (int X, int Z)> cell)
    {
        var y = height + 1;
        for (var i = 0; i < count; i++)
        {
            var (x, z) = cell(i);
            if (HeightAt(x, z) > height) return false;
            var chunk = chunks[(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift)];
            var id = chunk.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
            if (TreeGenerator.IsLeaf(id)) return false;
            if (BlockType.IsTreeWood(id)
                && chunk.GetLocalState(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask) != WoodState.Detached)
                return false;
        }

        return true;
    }

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
            Assert.InRange(length, WorldFeatureConfig.FallenLogMinLength, WorldFeatureConfig.FallenLogMaxLength);

            var chunks = new Dictionary<(int, int, int), Chunk>();
            for (var i = 0; i < length; i++)
            {
                var x = alongX ? sx + i : sx;
                var z = alongX ? sz : sz + i;
                var key = (x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift);
                if (!chunks.ContainsKey(key))
                    chunks[key] = ChunkAt(x, height + 1, z);
            }

            // The log goes into air only, so a log whose layer the rising ground, a trunk or a
            // crown already fills is cut short by design; the rule is checked on a clear floor.
            if (!LayerIsClearOfGroundAndTrees(chunks, height, length, i => alongX ? (sx + i, sz) : (sx, sz + i)))
                continue;

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
            if (biome is not (Biome.Forest or Biome.Taiga or Biome.Steppe or Biome.Savanna)) continue;
            var height = HeightAt(cx, cz);
            if (TerrainGenerator.IsWaterColumn(cx, cz, height)) continue;
            Assert.InRange(count, WorldFeatureConfig.BoulderMinBlocks, WorldFeatureConfig.BoulderMaxBlocks);
            Assert.Contains(blockId, new[] { BoulderGenerator.StoneBlock, BoulderGenerator.CobblestoneBlock });

            var chunks = new Dictionary<(int, int, int), Chunk>();
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var x = cx + dx;
                var z = cz + dz;
                var key = (x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift);
                if (!chunks.ContainsKey(key))
                    chunks[key] = ChunkAt(x, height + 1, z);
            }

            // The blob goes into air only, so one whose layer the rising ground, a trunk or a
            // crown already fills is cut short by design; the rule is checked on a clear floor.
            if (!LayerIsClearOfGroundAndTrees(chunks, height, 9, i => (cx + i / 3 - 1, cz + i % 3 - 1)))
                continue;

            var found = 0;
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var x = cx + dx;
                var z = cz + dz;
                var chunk = chunks[(x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift)];
                if (chunk.GetLocalBlock(x & Chunk.SizeMask, (height + 1) & Chunk.SizeMask, z & Chunk.SizeMask) == blockId)
                    found++;
            }

            Assert.InRange(found, count, count);
            return;
        }

        Assert.Fail("no boulder found near the origin");
    }
}
