using System.Collections.Concurrent;
using System.Collections.Generic;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Records the current partial-child overlap that needs a transition rule.</summary>
public sealed class LodProxyPartialCoverTests
{
    [Fact]
    public void OneFineChildLeavesCoarseRegionDrawnOverTheSameProxyFootprint()
    {
        var regions = new LodRegionSet(new ConcurrentDictionary<(int Level, int X, int Z), LodRegion>(),
            new List<LodRegion>());
        var coarse = new LodRegion(null, new LodMeshData(1, 0, 0, [], [], [], [], 0, 0, [], []));
        var child = new LodRegion(null, new LodMeshData(0, 0, 0, [], [], [], [], 0, 0, [], []));
        regions.AddRegion((1, 0, 0), coarse);
        regions.AddRegion((0, 0, 0), child);

        Assert.False(regions.IsCoveredByFinerLevel(1, 0, 0));
        var far = new LodTreeProxy(32, 32, 20, 50, 60, 28, 37, 28, 37, 2,
            11, 12, LodCrownProfile.Dome);
        var finePiece = far.Crown().ClipXZ(0, 0, 64, 64);
        var coarsePiece = far.Crown().ClipXZ(0, 0, 128, 128);
        Assert.True(finePiece.HasVolume && coarsePiece.HasVolume);
        Assert.Equal(finePiece.MinY, coarsePiece.MinY);
        Assert.Equal(finePiece.MaxY, coarsePiece.MaxY);
    }
}
