using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The GL-free arithmetic behind the LOD geometry pool: how a mesh's vertex and index counts turn
/// into pool units, and whether <see cref="ChunkGeometryPool.LodInitialUnits"/> is actually large
/// enough for the regions LOD can hold resident. Nothing here touches a GL context or a
/// <c>LodRegion</c> instance — only constants, statics and the <c>LodRegion.Levels</c> table — so
/// the sizing argument is asserted against exact numbers instead of eyeballed in the renderer.
/// </summary>
public sealed class LodGeometryPoolTests
{
    /// <summary>Integer (dx, dz) offsets inside a circle of the given region radius — the same
    /// <c>dx * dx + dz * dz &lt;= radius * radius</c> test LodManager uses both to generate
    /// regions (at RegionRadius) and to retain them (at RegionRadius + 1).</summary>
    private static int OffsetsWithin(int radius)
    {
        var count = 0;
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
            if (dx * dx + dz * dz <= radius * radius)
                count++;

        return count;
    }

    [Fact]
    public void UnitsForIsTheLargerOfTheTwoCeilings()
    {
        // One unit covers 64 vertices AND 96 indices, so a mesh needs whichever ceiling is bigger.
        Assert.Equal(0, ChunkGeometryPool.UnitsFor(0, 0));
        Assert.Equal(1, ChunkGeometryPool.UnitsFor(1, 1));
        Assert.Equal(1, ChunkGeometryPool.UnitsFor(64, 96));

        Assert.Equal(2, ChunkGeometryPool.UnitsFor(65, 96));  // vertex-bound
        Assert.Equal(2, ChunkGeometryPool.UnitsFor(64, 97));  // index-bound
        Assert.Equal(2, ChunkGeometryPool.UnitsFor(64, 192));
    }

    [Fact]
    public void ARegionShellWorstCaseRentIsExactlyThreeEightyFourUnits()
    {
        // The shell rent sits exactly on the quad ratio, so both ceilings agree.
        Assert.Equal(384, ChunkGeometryPool.UnitsFor(LodRegion.MeshVertexCapacity, LodRegion.MeshIndexCapacity));
    }

    [Fact]
    public void AFullWaterSheetIsExactlySixtyFourUnits()
    {
        Assert.Equal(64, ChunkGeometryPool.UnitsFor(LodRegion.WaterVertexCapacity, LodRegion.WaterIndexCapacity));
    }

    [Fact]
    public void AFlatRegionShellFloorsAtSixtyFourUnits()
    {
        // Every level is 32x32 = 1024 cells and each cell emits at least a top quad, so no region
        // shell is ever smaller than this — which is what the pool's degenerate floor rests on.
        Assert.Equal(64, ChunkGeometryPool.UnitsFor(1024 * 4, 1024 * 6));
    }

    [Fact]
    public void TheSettledRegionSetIsFiveNinetyOne()
    {
        // Derived from the level table rather than hard-coded: this is the number PERF_TODO quotes
        // and C5's capture measured.
        var total = 0;
        foreach (var level in LodRegion.Levels)
        {
            Assert.Equal(197, OffsetsWithin(level.RegionRadius));
            total += OffsetsWithin(level.RegionRadius);
        }

        Assert.Equal(3, LodRegion.Levels.Length);
        Assert.Equal(591, total);
    }

    [Fact]
    public void TheResidentCeilingIsSevenFiftyNine()
    {
        // Generation scans at RegionRadius; retention and the out-of-range upload drop both test
        // RegionRadius + 1. The extra band is streaming hysteresis and is occupied exactly while
        // the camera is moving, so this — not 591 — is what the pool has to hold.
        var total = 0;
        foreach (var level in LodRegion.Levels)
        {
            Assert.Equal(253, OffsetsWithin(level.RegionRadius + 1));
            total += OffsetsWithin(level.RegionRadius + 1);
        }

        Assert.Equal(759, total);
    }

    [Fact]
    public void TheLodPoolClearsTheDegenerateSeaHeavyFloor()
    {
        // One pool holds shells AND sheets, so the floor is the resident ceiling times both floors:
        // 759 * (64 shell + 64 sheet) = 97152 units.
        Assert.Equal(97152, 759 * (64 + 64));
        Assert.True(ChunkGeometryPool.LodInitialUnits >= 97152,
            $"LodInitialUnits {ChunkGeometryPool.LodInitialUnits} is below the degenerate sea-heavy floor of 97152 units.");
    }

    [Fact]
    public void TheLodPoolFitsThePathologicalSingleRegion()
    {
        // AddQuad refuses past ~65532 vertices, so 1024 units is the largest slice one region can
        // ever need; the pool must never have to grow to satisfy a single arrival.
        Assert.Equal(1024, ChunkGeometryPool.UnitsFor(65532, 98298));
        Assert.True(ChunkGeometryPool.LodInitialUnits > ChunkGeometryPool.UnitsFor(65532, 98298));
    }
}
