using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Lod;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>
/// Since R10 the LOD passes submit through <c>ChunkDrawBatch</c>, and
/// <c>glMultiDrawElementsIndirect</c> executes its commands in array order — so the two sorts
/// in <c>RenderManager</c> (<c>RenderLod</c>'s opaque list and <c>CullWater</c>'s water list)
/// ARE the submission order. They were lifted onto <see cref="LodRegion"/> as plain scalar
/// comparisons so the ordering can be pinned exactly, with no GL, no region instance and no
/// content load, exactly as GeometryAllocatorTests / CloudCopies / HalfResTarget /
/// ChunkScanFrontier / ParticleRing pin their own GL-free arithmetic.
///
/// The mirror pass is deliberately NOT covered here: its list is unsorted by design.
/// </summary>
public sealed class LodDrawOrderTests
{
    /// <summary>
    /// One hand-made draw list spanning all three LOD levels, with distances chosen so that
    /// no two entries tie on (level, distSq) and neither comparator's answer can be produced
    /// by accident from the input order.
    /// </summary>
    private static List<(int level, float distSq)> Regions() =>
    [
        (0, 400f),
        (2, 100f),
        (1, 900f),
        (2, 25f),
        (0, 100f),
        (1, 400f),
    ];

    [Fact]
    public void CoarsestLevelDrawsFirstThenFrontToBack()
    {
        var list = Regions();

        list.Sort((a, b) => LodRegion.CompareOpaqueDraw(a.level, a.distSq, b.level, b.distSq));

        Assert.Equal(
            [(2, 25f), (2, 100f), (1, 400f), (1, 900f), (0, 100f), (0, 400f)],
            list);
    }

    [Fact]
    public void FinestWaterSheetDrawsFirstThenBackToFront()
    {
        var list = Regions();

        list.Sort((a, b) => LodRegion.CompareWaterDraw(a.level, a.distSq, b.level, b.distSq));

        Assert.Equal(
            [(0, 400f), (0, 100f), (1, 900f), (1, 400f), (2, 100f), (2, 25f)],
            list);
    }

    [Fact]
    public void WithinOneLevelTheTieBreakIsDistanceOnly()
    {
        // Same level, so only the distance tie-break can decide: opaque draws the near one
        // first (front to back), water draws the far one first (back to front).
        Assert.True(LodRegion.CompareOpaqueDraw(1, 100f, 1, 900f) < 0);
        Assert.True(LodRegion.CompareWaterDraw(1, 100f, 1, 900f) > 0);

        // And an exact tie on both keys compares equal in both passes.
        Assert.Equal(0, LodRegion.CompareOpaqueDraw(1, 100f, 1, 100f));
        Assert.Equal(0, LodRegion.CompareWaterDraw(1, 100f, 1, 100f));
    }

    [Fact]
    public void TheTwoComparatorsDisagreeOnLevelOrder()
    {
        var opaque = Regions();
        opaque.Sort((a, b) => LodRegion.CompareOpaqueDraw(a.level, a.distSq, b.level, b.distSq));

        var water = Regions();
        water.Sort((a, b) => LodRegion.CompareWaterDraw(a.level, a.distSq, b.level, b.distSq));

        // The two passes deliberately run the levels in opposite directions: the opaque pass
        // wants the coarse shells underneath the fine ones, the water pass wants the finest
        // sheet down first so the coarser duplicates fail the depth test.
        Assert.Equal([2, 2, 1, 1, 0, 0], opaque.Select(r => r.level));
        Assert.Equal(opaque.Select(r => r.level).Reverse(), water.Select(r => r.level));
    }
}
