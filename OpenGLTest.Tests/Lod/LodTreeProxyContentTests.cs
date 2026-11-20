using System;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Content-facing leafless contract, separate from the cheap pure proxy facts.</summary>
[Collection("Worldgen shape cache")]
public sealed class LodTreeProxyContentTests
{
    [Fact]
    public void LeaflessCactusPlaceholderCannotBecomeAShortFarWoodProxy()
    {
        TestContent.EnsureLoaded();
        var cactus = TreeSpeciesRegistry.ByName["cactus"];
        Assert.False(cactus.HasLeaves);
        Assert.Throws<ArgumentException>(() => LodTreeProxy.FromFar(cactus, 12, -8, 40));
    }
}
