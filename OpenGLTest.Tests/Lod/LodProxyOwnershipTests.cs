using System;
using System.Collections.Generic;
using VoxelEngine.Lod;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Pure callback guards before any placement or batch work.</summary>
public sealed class LodProxyOwnershipTests
{
    [Fact]
    public void FullySuppliedRegionDoesNotEnumerateRootsOrPlanBoxes()
    {
        var supplied = new bool[4 * 4];
        Array.Fill(supplied, true);
        var sourceCalls = 0;
        var placementCalls = 0;
        var boxes = new List<LodProxyBox>();

        LodProxyOwnership.CollectFar(supplied, 4, 2, 0, 0, 32, 2,
            (x, z) => { sourceCalls++; return true; },
            (x, z) => { placementCalls++; return new LodProxyBox(x, 40, z, x + 1, 44, z + 1); }, boxes);

        Assert.False(LodProxyOwnership.HasGeneratedOwnedColumn(supplied, 4, 2));
        Assert.Equal(0, sourceCalls);
        Assert.Equal(0, placementCalls);
        Assert.Empty(boxes);
    }

    [Fact]
    public void MixedRegionOnlyResolvesGeneratedRootsAndClipsTheirPieces()
    {
        var supplied = new bool[4 * 4];
        Array.Fill(supplied, true);
        supplied[2 * 4 + 1] = false;
        var calledSupplied = 0;
        var calledGenerated = 0;
        var boxes = new List<LodProxyBox>();

        LodProxyOwnership.CollectFar(supplied, 4, 2, 0, 0, 32, 0,
            (x, z) => x < 16,
            (x, z) =>
            {
                if (x < 16) calledSupplied++;
                else calledGenerated++;
                return x == 16 && z == 8
                    ? new LodProxyBox(14, 40, 8, 19, 45, 11) : null;
            }, boxes);

        Assert.True(LodProxyOwnership.HasGeneratedOwnedColumn(supplied, 4, 2));
        Assert.Equal(0, calledSupplied);
        Assert.True(calledGenerated > 0);
        var generatedPiece = Assert.Single(boxes).ClipXZ(16, 0, 32, 16);
        Assert.True(generatedPiece.HasVolume);
        Assert.Equal(16f, generatedPiece.MinX);
        Assert.Equal(19f, generatedPiece.MaxX);
        Assert.False(boxes[0].ClipXZ(0, 16, 16, 32).HasVolume);
    }
}
