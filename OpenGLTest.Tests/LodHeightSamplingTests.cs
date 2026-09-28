using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Covers items C1 and C9. <see cref="LodRegion.BuildMeshData"/> now takes each LOD cell's
/// eroded height from a separable rectangular min — a per-cell-row horizontal pass over a
/// materialised fine-height grid, then a vertical scan per cell — instead of the fused
/// O(window^2) nested scan it replaced. Integer min is associative and commutative, so the two
/// must agree bit for bit; that is the entire acceptance criterion for the change, and it is
/// what these facts pin: at every level, at several region coordinates, with column caps active,
/// and exactly at the one-cell overrun past the region edge where the grid sizing is decided.
/// The <c>separableMin: false</c> seam keeps the original nested loop reachable to compare
/// against, the way <c>GenerateMeshData(manager, greedy: false)</c> backs
/// <c>GreedyMeshingTests</c>.
/// </summary>
[Collection("LOD column caps")]
public sealed class LodHeightSamplingTests
{
    public LodHeightSamplingTests() => TestContent.EnsureLoaded();

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

    /// <summary>An all-land level-<paramref name="level"/> region with relief, so the edge-cap fact
    /// moves real ground rather than a seabed already under water or a dead-flat shell.</summary>
    private static (int Rx, int Rz) FindLandRegion(int level)
    {
        for (var rx = 900; rx <= 1100; rx++)
        for (var rz = 0; rz <= 60; rz++)
        {
            var floor = RegionFloor(level, rx, rz);
            if (floor < 0) continue;
            if (RegionCeiling(level, rx, rz) - floor >= 6)
                return (rx, rz);
        }

        Assert.Fail("no relieved all-land level-0 region found near rx 900..1100");
        return (0, 0);
    }

    private static int RegionCeiling(int level, int rx, int rz)
    {
        var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var ceiling = int.MinValue;
        for (var cx = -1; cx <= span; cx++)
        for (var cz = -1; cz <= span; cz++)
            foreach (var h in Chunk.GetColumnHeights(rx * span + cx, rz * span + cz))
                if (h > ceiling) ceiling = h;
        return ceiling;
    }

    /// <summary>
    /// Full-output equality: every buffer the build emits, not just the vertices. A min that
    /// diverged only on a border cell would still move ColumnCyMin or the region AABB.
    /// </summary>
    private static void AssertSameMesh(LodMeshData expected, LodMeshData actual)
    {
        Assert.Equal(expected.Vertices.Count, actual.Vertices.Count);
        Assert.Equal(expected.Indices.Count, actual.Indices.Count);
        Assert.Equal(expected.WaterVertices.Count, actual.WaterVertices.Count);
        Assert.Equal(expected.WaterIndices.Count, actual.WaterIndices.Count);

        // SequenceEqual, not Assert.Equal(IEnumerable, IEnumerable): Vertex is a struct of
        // float-bearing Vector3/Vector2 fields, so xUnit's comparer would fall back to
        // reflective ValueType.Equals for each of ~21k elements.
        Assert.True(expected.Vertices.SequenceEqual(actual.Vertices));
        Assert.True(expected.Indices.SequenceEqual(actual.Indices));
        Assert.True(expected.WaterVertices.SequenceEqual(actual.WaterVertices));
        Assert.True(expected.WaterIndices.SequenceEqual(actual.WaterIndices));
        Assert.True(expected.ColumnCyMin.SequenceEqual(actual.ColumnCyMin));
        Assert.True(expected.ColumnCyMax.SequenceEqual(actual.ColumnCyMax));

        Assert.Equal(expected.YMin, actual.YMin);
        Assert.Equal(expected.YMax, actual.YMax);
    }

    [Fact]
    public void SeparableMinMatchesTheNestedWindowAtEveryLevel()
    {
        // These regions carry no caps, so the fact isolates the min algorithm itself: the grid
        // fill, the horizontal pass and the vertical pass against the scan they replaced.
        (int Rx, int Rz)[] regions = [(0, 0), (1, 0), (0, 1), (0, -1), (3, -2), (-1, -1)];
        var totalWater = 0;

        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            foreach (var (rx, rz) in regions)
            {
                var separable = LodRegion.BuildMeshData(level, rx, rz);
                var nested = LodRegion.BuildMeshData(level, rx, rz, null, separableMin: false);

                AssertSameMesh(nested, separable);

                // Positive control: an all-empty pair would compare equal and prove nothing.
                Assert.NotEmpty(separable.Vertices);
                totalWater += separable.WaterVertices.Count;
            }
        }

        // ...and the water path is exercised somewhere in the matrix — region (0, -1) covers the
        // bay perf-fly flies over, so sea cells (which read maxOwn, not the min) are included.
        Assert.True(totalWater > 0);
    }

    [Fact]
    public void SeparableMinMatchesTheNestedWindowUnderColumnCaps()
    {
        // ColumnCaps is process-global with no reset hook and xUnit runs test classes in
        // parallel, so this region sits far from every other test's regions: level 2 region
        // (555, -555) covers fine ~142080, while the rest of the suite stays near the origin
        // (and LodColumnCapTests sits at level 1 region (777, 777), fine ~99456).
        const int Level = 2, Rx = 555, Rz = -555;
        var spec = LodRegion.Levels[Level];
        var baseFineX = Rx * spec.RegionBlocks;
        var baseFineZ = Rz * spec.RegionBlocks;

        var before = LodRegion.BuildMeshData(Level, Rx, Rz);

        // Derived from a measured floor, not guessed as a relative offset: each stored cap is at
        // least 33 below the lowest terrain the region can sample, so it IS the minimum of every
        // 22x22 window that covers it and survives the >> cellShift quantisation. A relative cap
        // (h - 3 - k, say) would prove nothing — it need not beat a min taken over 484 real
        // terrain columns.
        var floor = RegionFloor(Level, Rx, Rz);
        for (var k = 0; k < 8; k++)
        {
            var fx = baseFineX + 17 + k * 29;
            var fz = baseFineZ + 41 + k * 23;
            Assert.True(LodRegion.LowerColumnBelow(fx, floor - 32 - k, fz));
        }

        var separable = LodRegion.BuildMeshData(Level, Rx, Rz);
        var nested = LodRegion.BuildMeshData(Level, Rx, Rz, null, separableMin: false);

        // The grid fill merges the C9 cap bucket in as it copies while the seam applies it per
        // sample inside FineHeight; a cap that reached one path and not the other shows here.
        AssertSameMesh(nested, separable);

        // Control: the caps must actually have moved the output, or the equality above is a
        // statement about two identical uncapped builds.
        Assert.False(before.Vertices.SequenceEqual(separable.Vertices));
    }

    [Fact]
    public void SampleWindowReachIsExactAtTheRegionEdge()
    {
        // The off-by-one a naive separable rewrite gets wrong. Level 0 is cellSize 2, margin 1,
        // so fineMargin is 3 and the westernmost fine column any window reaches is baseFineX - 3
        // (cell i = -1, dx = -margin). The grid is sized to exactly that one-cell overrun rather
        // than clamped against it, so a wrong origin reads a neighbouring row — a wrong height,
        // not an exception — and only a column-by-column reach test catches it.
        const int Level = 0;
        // The forest refactor moved the terrain under (999, 0), so scan for an all-land region
        // instead of hardcoding one: the cap below its floor must land under real ground for the
        // border skirt to move. Far from the other tests' caps (level 1 (777,777), level 2 (555,-555)).
        var (rx, rz) = FindLandRegion(Level);
        var spec = LodRegion.Levels[Level];
        var baseFineX = rx * spec.RegionBlocks;
        var baseFineZ = rz * spec.RegionBlocks;
        var floor = RegionFloor(Level, rx, rz);

        var baseline = LodRegion.BuildMeshData(Level, rx, rz);

        // One column short of the reach: outside every sample window, outside the grid, and the
        // ColumnCyMin loop reads raw uncapped heightmaps, so this must change nothing at all.
        Assert.True(LodRegion.LowerColumnBelow(baseFineX - 4, floor - 32, baseFineZ + 32));
        var outside = LodRegion.BuildMeshData(Level, rx, rz);
        AssertSameMesh(baseline, outside);

        // The first column a window does reach. Its border groundTop drives the -X skirt and the
        // AO of the i = 0 column, so dropping that whole border line below the region floor must
        // move geometry. A line rather than one column, so an unlucky flat spot at a single Z
        // cannot hide the effect.
        for (var fz = baseFineZ - 3; fz <= baseFineZ + spec.RegionBlocks + 3; fz += 2)
            Assert.True(LodRegion.LowerColumnBelow(baseFineX - 3, floor - 32, fz));
        var inside = LodRegion.BuildMeshData(Level, rx, rz);
        Assert.False(baseline.Vertices.SequenceEqual(inside.Vertices));

        // ...and the separable pass still agrees with the nested one at that extreme column.
        AssertSameMesh(LodRegion.BuildMeshData(Level, rx, rz, null, separableMin: false), inside);
    }
}
