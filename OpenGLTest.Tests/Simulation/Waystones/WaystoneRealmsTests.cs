using VoxelEngine.Simulation.Waystones;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>
/// A waystone's realm tag follows the realm bands: hell below the barrier, the sky in the island band, and between them
/// the surface where the waystone stands on its column's ground and underground where it lies well beneath it.
/// </summary>
public sealed class WaystoneRealmsTests
{
    [Fact]
    public void TheBandsDecideHellAndSkyWhateverTheSurface()
    {
        Assert.Equal(WaystoneRealm.Hell, WaystoneRealms.Classify(WorldGenerationConfig.HellTopY, 70));
        Assert.Equal(WaystoneRealm.Hell, WaystoneRealms.Classify(WorldGenerationConfig.HellTopY - 500, null));
        Assert.Equal(WaystoneRealm.Sky, WaystoneRealms.Classify(WorldGenerationConfig.SkyIslandBottomY, 70));
        Assert.Equal(WaystoneRealm.Sky, WaystoneRealms.Classify(WorldGenerationConfig.SkyIslandTopY, null));
        Assert.NotEqual(WaystoneRealm.Hell, WaystoneRealms.Classify(WorldGenerationConfig.BarrierTopY, null));
    }

    [Fact]
    public void BetweenTheBandsTheColumnsSurfaceSplitsSurfaceFromUnderground()
    {
        const int ground = 64;
        Assert.Equal(WaystoneRealm.Surface, WaystoneRealms.Classify(ground + 1, ground));
        Assert.Equal(WaystoneRealm.Surface, WaystoneRealms.Classify(ground - WaystoneRealms.UndergroundDepthBlocks + 1, ground));
        Assert.Equal(WaystoneRealm.Underground, WaystoneRealms.Classify(ground - WaystoneRealms.UndergroundDepthBlocks, ground));
        Assert.Equal(WaystoneRealm.Underground, WaystoneRealms.Classify(ground - 40, ground));
        Assert.Equal(WaystoneRealm.Surface, WaystoneRealms.Classify(ground - 40, ground - 40 - 1));
    }

    [Fact]
    public void EveryRealmHasItsOwnTag()
    {
        var tags = new System.Collections.Generic.HashSet<string>();
        foreach (var realm in System.Enum.GetValues<WaystoneRealm>())
            Assert.True(tags.Add(WaystoneRealms.Tag(realm)), $"{realm} shares its tag");
    }
}
