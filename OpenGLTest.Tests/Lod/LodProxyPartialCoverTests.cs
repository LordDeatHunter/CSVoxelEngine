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
        var coarse = LodLiveIntegrationTests.HeadlessRegion(1, 0, 0, 0, 32);
        var child = LodLiveIntegrationTests.HeadlessRegion(0, 0, 0, 0);
        child.Lifetime = LodRegion.MAX_LIFETIME;
        regions.AddRegion((1, 0, 0), coarse);
        regions.AddRegion((0, 0, 0), child);

        Assert.False(regions.IsCoveredByFinerLevel(1, 0, 0));
        var cover = new LodTreeCoverPass(regions);
        cover.Update(static (_, _, _, _) => false);
        Assert.False(coarse.TreeVisibleAt(0, 0));
        Assert.True(coarse.TreeVisibleAt(4, 0));
    }
}
