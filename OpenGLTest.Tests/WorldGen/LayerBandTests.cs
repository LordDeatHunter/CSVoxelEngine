using VoxelEngine.WorldGen.Caves;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Features.Underground;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Simulation.Waystones;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The realm layer bands (plans/LAYERS.md) keep their order whatever they are tuned to: hell lies
/// under the barrier, which lies under the sea; the barrier's solid core sits inside its ramps; the
/// solid cloud slab clears the terrain's relief ceiling and the drawn cloud layer; the sky-island
/// cloud sea lies between the slab and the islands above it; every hell layer is more than its
/// slab, so it holds a pocket; and the cave biome bands (plans/CAVE_BIOMES.md) stack in order above
/// the barrier and under the sea, their wavy edges never crossing, each exclusive cave ore growing
/// only inside the reach of its own biome.
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
    public void FormerHellAndBorderAreOverworldAndNewBoundaryIsShared()
    {
        TestContent.EnsureLoaded();
        // Historical coordinates are intentional: issue #10 requires an exact 200-block move.
        for (var y = -400; y <= -300; y++)
        {
            Assert.False(BarrierGenerator.IsBarrier(0, y, 0));
            Assert.False(HellGenerator.InHell(y));
            Assert.Equal(-1, LayerClimate.HellLayerIndex(y + 0.5f));
        }
        Assert.False(HellGenerator.InHell(-600));
        Assert.True(HellGenerator.InHell(-601));
        Assert.Equal(0, HellGenerator.LayerIndex(-601));
        Assert.Equal(0, LayerClimate.HellLayerIndex(-600.5f));
        Assert.Equal(WaystoneRealm.Hell, WaystoneRealms.Classify(-601, null));
        Assert.NotEqual(WaystoneRealm.Hell, WaystoneRealms.Classify(-600, null));
        Assert.True(BarrierGenerator.InCore(-530));
        Assert.True(BarrierGenerator.InCore(-570));
        Assert.False(BarrierGenerator.InCore(-529));
        Assert.False(BarrierGenerator.InCore(-571));
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

    [Fact]
    public void CaveBiomeBandsLieAboveTheBarrierInOrder()
    {
        Assert.True(V(WorldGenerationConfig.BarrierTopY)
                    < V(WorldGenerationConfig.CaveDeepTopY) - V(WorldGenerationConfig.CaveDeepDriftBlocks));
        Assert.True(V(WorldGenerationConfig.CaveDeepTopY) + V(WorldGenerationConfig.CaveDeepDriftBlocks)
                    < V(WorldGenerationConfig.CaveEchoBottomY) - V(WorldGenerationConfig.CaveEchoDriftBlocks));
        Assert.True(V(WorldGenerationConfig.CaveEchoBottomY) + V(WorldGenerationConfig.CaveEchoDriftBlocks)
                    < V(WorldGenerationConfig.SeaLevel));
        Assert.True(V(WorldGenerationConfig.CaveEchoCoverBlocks) >= 0f);
        Assert.True(V(WorldGenerationConfig.CaveEchoCoverJitterBlocks) >= 0f);
        Assert.True(V(WorldGenerationConfig.CaveFungalMaxValue) <= V(WorldGenerationConfig.CaveCrystalMinValue));
    }

    [Fact]
    public void EveryExclusiveCaveOreGrowsInsideItsBiomesReach()
    {
        TestContent.EnsureLoaded();
        (CaveBiome Biome, int HighestReach)[] reaches =
        [
            (CaveBiome.Crystal, CaveBiomes.SubBandTopY),
            (CaveBiome.Fungal, CaveBiomes.SubBandTopY),
            (CaveBiome.MagmaDeep, WorldGenerationConfig.CaveDeepTopY + WorldGenerationConfig.CaveDeepDriftBlocks),
        ];
        foreach (var (biome, highestReach) in reaches)
        {
            Assert.NotEqual(0, CaveOres.OreOf(biome));
            Assert.True(V(CaveOres.MaxYOf(biome)) > V(WorldGenerationConfig.BarrierTopY), biome.ToString());
            Assert.True(V(CaveOres.MaxYOf(biome)) <= V(highestReach), biome.ToString());
        }
    }
}
