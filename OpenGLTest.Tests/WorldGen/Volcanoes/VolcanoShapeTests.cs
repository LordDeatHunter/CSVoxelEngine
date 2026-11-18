using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Volcanoes;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Volcanoes;

/// <summary>Pure caldera geometry and classification rules; these controls need no world scan.</summary>
public sealed class VolcanoShapeTests
{
    private static readonly VolcanoSite Site = new(true, 0f, 0f, 100f, 20f, 120, 6, 100f);

    [Fact]
    public void OceanAndOldDesertWinTheSyntheticProvince()
    {
        const float province = 1f;
        Assert.Equal(Biome.Ocean, BiomeClassifier.Classify(1f, 0f, 1f, 1f, 1f, 0f, province));
        Assert.Equal(Biome.Desert, BiomeClassifier.Classify(0f, 0f, 1f, 1f, 1f, 0f, province));
        Assert.Equal(Biome.Charred, BiomeClassifier.Classify(0f, 0f, 1f, 0f, 0f, 1f, province));
        Assert.Equal(Biome.Forest, BiomeClassifier.Classify(0f, 0f, 0f, 0f, 0f));
    }

    [Fact]
    public void ConeHasExactOutsideIdentityAndAssignedFlatCrater()
    {
        const float oldHeight = 73.125f;
        Assert.Equal(oldHeight, VolcanoShape.HeightAt(default, 0f, 0f, oldHeight));
        Assert.Equal(oldHeight, VolcanoShape.HeightAt(Site, Site.Radius, 0f, oldHeight));
        Assert.Equal(oldHeight, VolcanoShape.HeightAt(Site, Site.Radius + 1f, 0f, oldHeight));
        Assert.Equal(Site.Floor, VolcanoShape.HeightAt(Site, Site.CraterRadius - 0.1f, 0f, oldHeight));
        Assert.False(VolcanoShape.InCrater(Site, Site.CraterRadius, 0f));
        Assert.True(VolcanoShape.InGuard(Site, Site.CraterRadius, 0f));
        Assert.False(VolcanoShape.InGuard(Site,
            Site.CraterRadius + WorldGenerationConfig.VolcanoGuard, 0f));
        var shoulder = VolcanoShape.HeightAt(Site, Site.CraterRadius +
            WorldGenerationConfig.VolcanoWallWidth, 0f, oldHeight);
        Assert.True(shoulder > Site.Floor);
        Assert.True(shoulder <= WorldGenerationConfig.DynamicReliefCeilingHeight);
    }

    [Fact]
    public void EveryLavaCellHasLinedBedAndFourHorizontalNeighbors()
    {
        var lavaCount = 0;
        for (var x = -(int)Site.CraterRadius; x <= Site.CraterRadius; x++)
        for (var z = -(int)Site.CraterRadius; z <= Site.CraterRadius; z++)
        for (var y = Site.Floor - Site.LakeDepth + 1; y <= Site.Floor; y++)
        {
            if (VolcanoShape.Classify(Site, x, y, z, Site.Floor) != VolcanoCell.Lava) continue;
            lavaCount++;
            Assert.Equal(VolcanoCell.Lining,
                VolcanoShape.Classify(Site, x, Site.Floor - Site.LakeDepth, z, Site.Floor));
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var neighbor = VolcanoShape.Classify(Site, x + dx, y, z + dz, Site.Floor);
                Assert.True(neighbor is VolcanoCell.Lava or VolcanoCell.Lining);
            }
        }
        Assert.True(lavaCount > 0);
        Assert.Equal(VolcanoCell.None,
            VolcanoShape.Classify(Site, 0, Site.Floor, 0, Site.Floor - 1));
    }
}
