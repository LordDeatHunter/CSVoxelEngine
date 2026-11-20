using System.Collections.Concurrent;
using System.Collections.Generic;
using VoxelEngine.Lod;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>A partial finer ring cannot erase a coarse crown outside the ready child.</summary>
public sealed class LodProxyPartialCoverTests
{
    [Fact]
    public void OneReadyFineChildOnlyReplacesItsOwnCoarseTreeTiles()
    {
        var regions = new LodRegionSet(new ConcurrentDictionary<(int Level, int X, int Z), LodRegion>(),
            new List<LodRegion>());
        var coarse = new LodRegion(null, new LodMeshData(1, 0, 0, [], [], [], [], 0, 0, [], []));
        var child = new LodRegion(null, new LodMeshData(0, 0, 0, [], [], [], [], 0, 0, [], []));
        regions.AddRegion((1, 0, 0), coarse);
        regions.AddRegion((0, 0, 0), child);

        Assert.False(regions.IsCoveredByFinerLevel(1, 0, 0));
        var supplied = new bool[8 * 8];
        var ready = new bool[8 * 8];
        var full = new bool[8 * 8];
        LodTreeCoverage.MarkChildReady(128, 0, 0, ready);
        var mask = LodTreeCoverage.Build(128, 1, supplied, ready, full);
        Assert.False(mask.Visible(3, 3));
        Assert.True(mask.Visible(4, 3));
        Assert.True(mask.Visible(3, 4));
    }
}
