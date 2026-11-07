using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The realm layer bands (plans/LAYERS.md) keep their order whatever they are tuned to: hell lies
/// under the barrier, which lies under the sea; the barrier's solid core sits inside its ramps; the
/// solid cloud slab clears the terrain's relief ceiling and the drawn cloud layer; the sky-island
/// cloud sea lies between the slab and the islands above it; and every hell layer is more than its
/// slab, so it holds a pocket.
/// </summary>
public sealed class LayerBandTests
{
    // Copied into locals so the comparisons are judged at run time, not folded as constants
    private static float V(float value) => value;

    [Fact]
    public void HellLiesUnderTheBarrierWhichLiesUnderTheSea()
    {
        Assert.True(V(WorldGenerationConfig.BarrierTopY) < V(WorldGenerationConfig.SeaLevel));
        Assert.True(V(WorldGenerationConfig.HellTopY) < V(WorldGenerationConfig.BarrierBottomY));
        Assert.True(V(WorldGenerationConfig.HellSlabThicknessBlocks) > 0f);
        Assert.True(V(WorldGenerationConfig.HellSlabThicknessBlocks) < V(WorldGenerationConfig.HellLayerPeriodBlocks));
    }

    [Fact]
    public void TheBarrierCoreSitsInsideItsRamps()
    {
        Assert.True(V(WorldGenerationConfig.BarrierTopY) > V(WorldGenerationConfig.BarrierFullTopY));
        Assert.True(V(WorldGenerationConfig.BarrierFullTopY) > V(WorldGenerationConfig.BarrierFullBottomY));
        Assert.True(V(WorldGenerationConfig.BarrierFullBottomY) > V(WorldGenerationConfig.BarrierBottomY));
    }

    [Fact]
    public void TheCloudSlabClearsTheTerrainAndTheDrawnClouds()
    {
        Assert.True(V(WorldGenerationConfig.CloudSlabBottomY) > V(WorldGenerationConfig.ReliefCeilingHeight));
        Assert.True(V(WorldGenerationConfig.CloudSlabBottomY)
                    > V(SkyConfig.CloudHeight) + V(SkyConfig.CloudLayerThickness) * SkyConfig.CloudMaxLevels);
        Assert.True(V(WorldGenerationConfig.CloudSlabBottomY) <= V(WorldGenerationConfig.CloudSlabTopY));
    }

    [Fact]
    public void TheSkyIslandsFloatOverTheirCloudSeaAboveTheSlab()
    {
        Assert.True(V(SkyConfig.SkyCloudSeaHeight) > V(WorldGenerationConfig.CloudSlabTopY));
        Assert.True(V(SkyConfig.SkyCloudSeaHeight) + V(SkyConfig.SkyCloudSeaThickness)
                    < V(WorldGenerationConfig.SkyIslandBottomY));
        Assert.True(V(WorldGenerationConfig.SkyIslandBottomY) < V(WorldGenerationConfig.SkyIslandTopY));
    }
}
