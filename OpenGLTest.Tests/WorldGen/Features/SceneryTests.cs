using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// The floor scenery: a fallen log stamps a whole 3-5 cell hollow log of its biome's tree species
/// at one height, its shell turned along the log, and a boulder stamps a 2-4 cell blob of stone or
/// cobblestone on the forest or taiga floor, both only into air and clipped at chunk borders. In
/// the seed collection because the log scan generates chunks far from the origin under the
/// default seed.
/// </summary>
[Collection("Worldgen seed")]
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

    // How far (in fallen-log cells) the scan looks for a log in every biome that has one
    private const int MaxLogRing = 1500;

    /// <summary>The cells of the square ring at Chebyshev distance <paramref name="r"/> from cell (0, 0).</summary>
    private static IEnumerable<(int X, int Z)> Ring(int r)
    {
        if (r == 0)
        {
            yield return (0, 0);
            yield break;
        }

        for (var i = -r; i <= r; i++)
        {
            yield return (i, -r);
            yield return (i, r);
        }

        for (var i = -r + 1; i <= r - 1; i++)
        {
            yield return (-r, i);
            yield return (r, i);
        }
    }

    // Whether the line through the cell's center along the given axis (0 = X, 1 = Y, 2 = Z) runs
    // inside the box: the other two coordinates of the center lie strictly within its extent
    private static bool LineThroughCenterCrosses(BlockHitbox box, int axis)
    {
        for (var a = 0; a < 3; a++)
        {
            if (a == axis) continue;
            if (!(box.Min[a] < 0.5f && 0.5f < box.Max[a])) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a fallen log's stamped facing turns its shell along the log: the line through the
    /// cell's center along the log's axis misses every box (the hole is open end to end), while the
    /// horizontal line across it runs into a wall.
    /// </summary>
    private static bool ShellIsOpenAlong(byte block, byte state, bool alongX)
    {
        var shapes = BlockType.ById[block]!.RotatedHitboxes!;
        var boxes = shapes[BlockType.ShapeIndex(state, shapes.Length)];
        var along = alongX ? 0 : 2;
        var across = alongX ? 2 : 0;
        var open = true;
        var walled = false;
        foreach (var box in boxes)
        {
            if (LineThroughCenterCrosses(box, along)) open = false;
            if (LineThroughCenterCrosses(box, across)) walled = true;
        }

        return open && walled;
    }

    [Fact]
    public void AFallenLogLiesWholeAsTheHollowLogOfItsBiomeAlongItsAxis()
    {
        var pending = new HashSet<Biome>();
        foreach (var biome in Enum.GetValues<Biome>())
            if (FallenLogGenerator.HollowLogFor(biome) != BlockType.Air)
                pending.Add(biome);
        Assert.NotEmpty(pending);

        for (var r = 0; r <= MaxLogRing && pending.Count > 0; r++)
        foreach (var (cellX, cellZ) in Ring(r))
        {
            // Cheap filter first: only a candidate in a biome still to be checked is placed and
            // generated, so the scan's cost stays a hash and a biome read per candidate
            if (!FallenLogGenerator.TryGetLog(cellX, cellZ, out var sx, out var sz, out _, out _)) continue;
            var biome = TerrainGenerator.BiomeAt(sx, sz);
            if (!pending.Contains(biome)) continue;
            if (!FallenLogGenerator.TryGetPlacedLog(cellX, cellZ, out sx, out sz, out var length, out var alongX,
                    out var height, out var log))
                continue;
            Assert.InRange(length, WorldFeatureConfig.FallenLogMinLength, WorldFeatureConfig.FallenLogMaxLength);
            Assert.Equal(FallenLogGenerator.HollowLogFor(biome), log);

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
            // A log never leaves its own cell, so two logs never cross.
            if (!LayerIsClearOfGroundAndTrees(chunks, height, length, i => alongX ? (sx + i, sz) : (sx, sz + i)))
                continue;

            // Every cell of the log is there at one height, the hollow log of its start column's
            // biome, its shell turned so the hole runs along the log
            for (var i = 0; i < length; i++)
            {
                var x = alongX ? sx + i : sx;
                var z = alongX ? sz : sz + i;
                var chunk = chunks[(x >> Chunk.SizeShift, (height + 1) >> Chunk.SizeShift, z >> Chunk.SizeShift)];
                var lx = x & Chunk.SizeMask;
                var ly = (height + 1) & Chunk.SizeMask;
                var lz = z & Chunk.SizeMask;
                Assert.Equal(log, chunk.GetLocalBlock(lx, ly, lz));
                Assert.True(ShellIsOpenAlong(log, chunk.GetLocalState(lx, ly, lz), alongX),
                    $"the {biome} log cell at ({x}, {height + 1}, {z}) is not open along {(alongX ? "X" : "Z")}");
            }

            pending.Remove(biome);
        }

        Assert.True(pending.Count == 0,
            $"no clear fallen log found within {MaxLogRing} cells in {string.Join(", ", pending)}");
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
