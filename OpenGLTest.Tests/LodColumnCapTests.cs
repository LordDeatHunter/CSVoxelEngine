using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Covers item C9: LOD column height caps are bucketed by chunk column and resolved once per
/// prefetched heightmap instead of being probed per height sample. The observable contract is
/// unchanged, so these facts pin it end to end — a cap written through
/// <see cref="LodRegion.LowerColumnBelow"/> must still reach a rebuilt region's heights, and the
/// bucket write must be visible to the very next read of the same bucket.
/// </summary>
public sealed class LodColumnCapTests
{
    public LodColumnCapTests() => TestContent.EnsureLoaded();

    /// <summary>
    /// The lowest terrain height anywhere the given region can sample — its chunk columns plus the
    /// 1-chunk border BuildMeshData prefetches. Capping a column below this is guaranteed to become
    /// the minimum of every sample window that covers it, so a test never has to guess whether a
    /// relative cap survives a 22x22 min and the &gt;&gt; cellShift quantisation.
    /// </summary>
    private static int RegionFloor(int level, int rx, int rz)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var floor = int.MaxValue;
        for (var cx = -1; cx <= span; cx++)
        for (var cz = -1; cz <= span; cz++)
            foreach (var h in Chunk.GetColumnHeights(rx * span + cx, rz * span + cz))
                if (h < floor) floor = h;
        return floor;
    }

    [Fact]
    public void MinedColumnCapsPropagateIntoARebuiltRegion()
    {
        // ColumnCaps is process-global, reset only by WorldSeed.Apply (which the parallel suite
        // never calls), and xUnit runs test classes in parallel, so this region sits far away from every other test's regions: region index
        // 777+ covers fine ~49728, while the rest of the suite stays near the origin (region
        // index ~5). The climate rework moved the biomes around, so the region is chosen as the
        // first far frozen one rather than a fixed index: frozen land carries no canopy, and a
        // canopy would bury the ground the cap lowers, leaving the drawn shell unchanged.
        var spec = LodRegion.Levels[1];
        var rx = 777;
        var rz = 777;
        while (TerrainGenerator.BiomeAt(rx * spec.RegionBlocks + 64, rz * spec.RegionBlocks + 64) != Biome.Frozen)
            rx++;
        var baseFineX = rx * spec.RegionBlocks;
        var baseFineZ = rz * spec.RegionBlocks;
        var fx = baseFineX + 64;   // chunk column (4, 4) of this region (regionChunkSpan 8)
        var fz = baseFineZ + 64;

        var h = Chunk.GetColumnHeights(fx >> Chunk.SizeShift, fz >> Chunk.SizeShift)
            [((fx & Chunk.SizeMask) << Chunk.SizeShift) | (fz & Chunk.SizeMask)];

        var before = LodRegion.BuildMeshData(1, rx, rz);

        // Above the terrain (e.g. a player-placed block): nothing to lower, no rebuild needed.
        Assert.False(LodRegion.LowerColumnBelow(fx, h + 5, fz));

        // Derived from a measured floor rather than guessed as a relative offset, so the stored
        // cap (floor - 33) is strictly below every other sample in the region and is therefore
        // guaranteed to win every window minimum that covers this column.
        var floor = RegionFloor(1, rx, rz);
        Assert.True(LodRegion.LowerColumnBelow(fx, floor - 32, fz));

        // The C9 write/read round trip: floor - 20 > floor - 33, so this must be refused — which
        // is only true if the previous call's bucket element write is visible to this read.
        Assert.False(LodRegion.LowerColumnBelow(fx, floor - 20, fz));

        var after = LodRegion.BuildMeshData(1, rx, rz);

        // The cap sits 33 below the region floor, so groundTop for every cell covering this
        // column drops by at least 33 >> cellShift (cellSize 4) = 8 cells, i.e. yLo =
        // cellSize * (minTop + 1) drops by at least 32 blocks and yLo >> 4 by at least 2.
        Assert.True(after.ColumnCyMin[4 * 8 + 4] < before.ColumnCyMin[4 * 8 + 4]);

        // Control: chunk column (0, 0)'s widest sample reach is fine offset 20, far short of 64.
        Assert.Equal(before.ColumnCyMin[0], after.ColumnCyMin[0]);

        // ...and the geometry really moved. SequenceEqual rather than Assert.Equal over the
        // lists: Vertex is a float-bearing struct, so xUnit would fall back to reflective
        // ValueType.Equals per element over ~20k vertices.
        Assert.False(before.Vertices.SequenceEqual(after.Vertices));
    }
}
