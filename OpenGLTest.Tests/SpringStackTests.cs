using System;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Hot springs and sea stacks: a spring's site holds one flat level below its centre's land
/// height across the disc, and a sea stack raises only a shallow ocean column above the sea.
/// </summary>
public sealed class SpringStackTests
{
    public SpringStackTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ASeaStackRaisesOnlyShallowOceanColumns()
    {
        // A real site from the pure hash
        var found = false;
        var centreX = 0;
        var centreZ = 0;
        var top = 0;
        for (var cellX = -40; cellX <= 40 && !found; cellX++)
        for (var cellZ = -40; cellZ <= 40 && !found; cellZ++)
        {
            if (!SeaStackGenerator.TryGetStack(cellX, cellZ, out centreX, out centreZ, out top)) continue;
            found = true;
        }
        Assert.True(found, "no sea stack candidate");

        var shallow = WorldGenerationConfig.SeaLevel - 3;
        Assert.Equal(top, SeaStackGenerator.TopAt(centreX, centreZ, shallow, Biome.Ocean));
        Assert.InRange(top, WorldGenerationConfig.SeaLevel + WorldGenerationConfig.SeaStackMinHeight,
            WorldGenerationConfig.SeaLevel + WorldGenerationConfig.SeaStackMaxHeight);

        // The pillar tapers: a column a block off the centre sits lower
        Assert.True(SeaStackGenerator.TopAt(centreX + 1, centreZ, shallow, Biome.Ocean) < top);

        // Deep water, land and a column already above the sea are left alone
        Assert.Equal(WorldGenerationConfig.SeaLevel - 20,
            SeaStackGenerator.TopAt(centreX, centreZ, WorldGenerationConfig.SeaLevel - 20, Biome.Ocean));
        Assert.Equal(shallow, SeaStackGenerator.TopAt(centreX, centreZ, shallow, Biome.Forest));
        Assert.Equal(WorldGenerationConfig.SeaLevel + 5,
            SeaStackGenerator.TopAt(centreX, centreZ, WorldGenerationConfig.SeaLevel + 5, Biome.Ocean));
    }

    [Fact]
    public void ASpringSiteHoldsOneFlatLevelBelowItsCentre()
    {
        // A real cold land site from the pure hash (the climate gate makes them sparse, so the
        // scan walks whole 128-block cells)
        var found = false;
        var level = 0;
        for (var cellX = -60; cellX <= 60 && !found; cellX++)
        for (var cellZ = -60; cellZ <= 60 && !found; cellZ++)
        {
            if (!HotSpringGenerator.TryGetSite(cellX << WorldGenerationConfig.SpringCellShift,
                    cellZ << WorldGenerationConfig.SpringCellShift, out level))
                continue;
            found = true;
        }
        Assert.True(found, "no hot spring site found in the scanned cells");

        // The site's level is its centre's land height less one, and the disc reaches a cell away
        Assert.True(HotSpringGenerator.IsSite(0, 0) == HotSpringGenerator.TryGetSite(0, 0, out _));
        var siteX = 0;
        var siteZ = 0;
        for (var cellX = -60; cellX <= 60 && siteX == 0 && siteZ == 0; cellX++)
        for (var cellZ = -60; cellZ <= 60 && siteX == 0 && siteZ == 0; cellZ++)
        {
            if (HotSpringGenerator.TryGetSite(cellX << WorldGenerationConfig.SpringCellShift,
                    cellZ << WorldGenerationConfig.SpringCellShift, out var siteLevel))
            {
                siteX = cellX << WorldGenerationConfig.SpringCellShift;
                siteZ = cellZ << WorldGenerationConfig.SpringCellShift;
                level = siteLevel;
            }
        }
        Assert.True(HotSpringGenerator.IsSite(siteX, siteZ));
        Assert.True(HotSpringGenerator.IsSite(siteX + WorldGenerationConfig.SpringRadius - 1, siteZ));
        Assert.False(HotSpringGenerator.IsSite(siteX + WorldGenerationConfig.SpringRadius + 1, siteZ));
        // The level is one under the centre's land height, so the pond digs in rather than floods
        Assert.True(level < (int)TerrainGenerator.LandHeight(siteX, siteZ, out _, out _, out _, out _));
    }
}
