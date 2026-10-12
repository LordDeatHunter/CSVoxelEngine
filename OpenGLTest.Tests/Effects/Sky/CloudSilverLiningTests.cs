using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The cloud layer's silver lining (<see cref="CloudSilverLining"/>, the twin of the lining term in
/// cloud-fragment.glsl): when it shows, where around the sun, and that it lights thin cloud rather
/// than the heart of a thick one.
/// </summary>
public sealed class CloudSilverLiningTests
{
    // A tiny test grid: the field size is fixed, so each of its cells is a wide square.
    private const int Cells = 8;
    private static float CellBlocks => WorldGenerationConfig.CloudFieldSize / Cells;
    private static readonly byte OneLayer = (byte)MathF.Round(255f / WorldGenerationConfig.CloudMaxLevels);

    [Fact]
    public void ThereIsNoLiningWithTheSunAtOrBelowTheHorizon()
    {
        for (var e = -1f; e <= 0f; e += 0.01f)
            Assert.Equal(0f, CloudSilverLining.Strength(e));
        Assert.Equal(0f, CloudSilverLining.Strength(0f));
    }

    [Fact]
    public void TheLiningStrengthensAsTheSunClimbsAndThenHoldsAtItsCap()
    {
        var previous = 0f;
        for (var e = 0f; e <= 1f; e += 0.005f)
        {
            var s = CloudSilverLining.Strength(e);
            Assert.True(s >= previous, $"the lining weakened from {previous} to {s} as the sun climbed ({e})");
            Assert.InRange(s, 0f, WorldGenerationConfig.CloudSilverLiningStrength);
            previous = s;
        }

        Assert.Equal(WorldGenerationConfig.CloudSilverLiningStrength, previous, 5);
    }

    [Fact]
    public void TheLiningGrowsTowardTheSunAndIsGoneAQuarterTurnAway()
    {
        Assert.Equal(0f, CloudSilverLining.Weight(0f, 1f));
        Assert.Equal(0f, CloudSilverLining.Weight(-0.7f, 1f));
        var previous = 0f;
        for (var degrees = 90f; degrees >= 0f; degrees -= 0.5f)
        {
            var w = CloudSilverLining.Weight(MathUtils.CosDeg(degrees), 1f);
            Assert.True(w >= previous - 1e-6f, $"the lining fell turning toward the sun ({degrees} degrees off)");
            previous = w;
        }
    }

    [Fact]
    public void ThickerCloudCarriesLessOfTheLining()
    {
        foreach (var cos in new[] { 0.9f, 0.99f, 1f })
        {
            var previous = CloudSilverLining.Weight(cos, 0f);
            Assert.Equal(0f, previous);
            for (var thin = 0f; thin <= 1f; thin += 0.05f)
            {
                var w = CloudSilverLining.Weight(cos, thin);
                Assert.True(w >= previous, "a thinner cloud carried less of the lining");
                previous = w;
            }
        }
    }

    [Fact]
    public void AnEdgeTheViewLeavesAtOnceIsThinAndTheHeartOfAThickCloudIsNot()
    {
        var grid = new byte[Cells * Cells];
        grid[1 * Cells + 1] = 255; // cell (1, 1), two layers
        var midY = WorldGenerationConfig.CloudHeight + WorldGenerationConfig.CloudLayerThickness;
        var z = 1.5f * CellBlocks;

        // On the cell's east wall, looking further east, out of the cloud: nothing behind the surface
        var eastWall = new Vector3(2f * CellBlocks, midY, z);
        Assert.Equal(1f, CloudSilverLining.Thinness(grid, Cells, eastWall, Vector3.UnitX, Vector2.Zero));

        // On its west wall, looking east into it: the whole sampled ray stays inside the cloud
        var westWall = new Vector3(CellBlocks, midY, z);
        Assert.Equal(0f, CloudSilverLining.Thinness(grid, Cells, westWall, Vector3.UnitX, Vector2.Zero));
    }

    [Fact]
    public void AOneLayerCloudIsThinnerThanATwoLayerOneSeenTheSameWay()
    {
        var bottom = new Vector3(1.5f * CellBlocks, WorldGenerationConfig.CloudHeight, 1.5f * CellBlocks);
        var climbing = Vector3.Normalize(new Vector3(1f, 1f, 0f));

        var thin = new byte[Cells * Cells];
        thin[1 * Cells + 1] = OneLayer;
        var thick = new byte[Cells * Cells];
        thick[1 * Cells + 1] = 255;

        Assert.True(CloudSilverLining.Thinness(thin, Cells, bottom, climbing, Vector2.Zero)
                    > CloudSilverLining.Thinness(thick, Cells, bottom, climbing, Vector2.Zero));
    }
}
