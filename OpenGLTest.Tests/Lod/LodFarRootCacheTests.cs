using System;
using System.Collections.Generic;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Pure generated-root cache rules, with no content or world initialization.</summary>
public sealed class LodFarRootCacheTests
{
    private static LodTreeProxy? Root(int x, int z) =>
        (x, z) is (-17, -1) or (-16, 0) or (-1, -1) or (0, 0) or (5, 5)
            ? new LodTreeProxy(x, z, 20, 27, 31, x - 2, x + 3, z - 2, z + 3,
                1, 11, 12, LodCrownProfile.Dome) : null;

    [Fact]
    public void CachedPartialAndNegativeWindowsKeepPointWalkOrderAndValues()
    {
        var supplied = new bool[4 * 4];
        var cache = new LodFarRootCache(Root, 32, 4);
        foreach (var (x0, z0, size, reach) in new[]
                 { (-24, -8, 32, 3), (-17, -2, 16, 1), (-2, -2, 16, 0) })
        {
            var original = new List<LodTreeProxy>();
            var cached = new List<LodTreeProxy>();
            bool Supplies(int x, int z) => x >= 0 && z < 0;
            LodProxyOwnership.CollectFar(supplied, 4, 2, x0, z0, size, reach,
                Supplies, Root, original);
            LodProxyOwnership.CollectFarCached(supplied, 4, 2, x0, z0, size, reach,
                Supplies, cache, cached);
            Assert.Equal(original, cached);
        }
    }

    [Fact]
    public void EmptyHitsEvictionAndResetPreservePureValues()
    {
        var calls = 0;
        var cache = new LodFarRootCache((x, z) => { calls++; return Root(x, z); }, 4, 2);
        var negative = cache.Column(-2, -1);
        Assert.Contains(negative, r => r.RootX == -17 && r.RootZ == -1);
        var afterCold = calls;
        Assert.Equal(negative, cache.Column(-2, -1));
        Assert.Equal(afterCold, calls);

        var empty = cache.Column(10, 10);
        Assert.Empty(empty);
        var afterEmpty = calls;
        Assert.Empty(cache.Column(10, 10));
        Assert.Equal(afterEmpty, calls);

        for (var cx = 11; cx < 20; cx++) cache.Column(cx, 10);
        var beforeRebuild = calls;
        Assert.Equal(negative, cache.Column(-2, -1));
        Assert.True(calls > beforeRebuild);
        cache.Reset();
        var beforeReset = calls;
        Assert.Equal(negative, cache.Column(-2, -1));
        Assert.True(calls > beforeReset);
    }

    [Fact]
    public void SuppliedOwnershipStopsColdAndWarmMetadataReads()
    {
        var supplied = new bool[4 * 4];
        var scans = 0;
        var cache = new LodFarRootCache((x, z) => { scans++; return Root(x, z); }, 8, 2);
        var roots = new List<LodTreeProxy>();
        Array.Fill(supplied, true);
        LodProxyOwnership.CollectFarCached(supplied, 4, 2, -16, -16, 32, 0,
            (_, _) => true, cache, roots);
        Assert.Empty(roots);
        Assert.Equal(0, scans);

        Array.Fill(supplied, false);
        LodProxyOwnership.CollectFarCached(supplied, 4, 2, -16, -16, 32, 0,
            (_, _) => false, cache, roots);
        Assert.NotEmpty(roots);
        roots.Clear();
        var warmScans = scans;
        LodProxyOwnership.CollectFarCached(supplied, 4, 2, -16, -16, 32, 0,
            (x, z) => x < 0 || z < 0, cache, roots);
        Assert.All(roots, root => Assert.True(root.RootX >= 0 && root.RootZ >= 0));
        Assert.Equal(warmScans, scans);
    }
}
