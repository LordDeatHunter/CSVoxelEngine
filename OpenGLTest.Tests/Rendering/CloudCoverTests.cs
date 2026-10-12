using System;
using System.Numerics;
using VoxelEngine.Rendering;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// The cloud cover along a ray (<see cref="CloudCover"/>, the twin of <c>CloudCoverAlongRay</c> in
/// sky-fragment.glsl): what hides the stars behind a cloud.
/// </summary>
public sealed class CloudCoverTests
{
    private static readonly float Below = WorldGenerationConfig.CloudHeight - 20f;

    // A tiny test grid: the field size is fixed, so each of its cells is a wide square.
    private const int Cells = 8;
    private static float CellBlocks => WorldGenerationConfig.CloudFieldSize / Cells;

    [Fact]
    public void TheLevelReadInvertsTheBakeForEveryLevel()
    {
        var max = WorldGenerationConfig.CloudMaxLevels;
        var levels = new byte[Cells, Cells];
        for (var x = 0; x < Cells; x++)
        for (var z = 0; z < Cells; z++)
            levels[x, z] = (byte)((x + z) % (max + 1));
        var coverage = CloudField.BuildShadowCoverage(levels, max);

        for (var x = 0; x < Cells; x++)
        for (var z = 0; z < Cells; z++)
            Assert.Equal(levels[x, z],
                CloudCover.LevelAt(coverage, Cells, (x + 0.5f) * CellBlocks, (z + 0.5f) * CellBlocks, Vector2.Zero));
    }

    [Fact]
    public void ACellCoversExactlyItsOwnSquareAndTheFieldRepeats()
    {
        var grid = new byte[Cells * Cells];
        grid[2 * Cells + 3] = 255; // cell (3, 2)
        const float inset = 0.01f;
        var x0 = 3 * CellBlocks;
        var z0 = 2 * CellBlocks;
        var drift = new Vector2(37f, 1500f);

        Assert.True(CloudCover.LevelAt(grid, Cells, x0 + inset + drift.X, z0 + inset + drift.Y, drift) > 0);
        Assert.True(CloudCover.LevelAt(grid, Cells, x0 + CellBlocks - inset + drift.X, z0 + CellBlocks - inset + drift.Y, drift) > 0);
        Assert.Equal(0, CloudCover.LevelAt(grid, Cells, x0 - inset + drift.X, z0 + inset + drift.Y, drift));
        Assert.Equal(0, CloudCover.LevelAt(grid, Cells, x0 + CellBlocks + inset + drift.X, z0 + inset + drift.Y, drift));
        var field = WorldGenerationConfig.CloudFieldSize;
        Assert.True(CloudCover.LevelAt(grid, Cells, x0 + 1f + drift.X - 3 * field, z0 + 1f + drift.Y + 2 * field, drift) > 0);
    }

    [Fact]
    public void ARayThatNeverMeetsTheLayerIsUncovered()
    {
        var full = Uniform(255);
        Assert.Equal(0f, Cover(full, new Vector3(0f, Below, 0f), new Vector3(0f, -1f, 0f)));
        Assert.Equal(0f, Cover(full, new Vector3(0f, Below, 0f), new Vector3(1f, 0f, 0f)));
        // from at or above the base the clouds are below the eye
        Assert.Equal(0f, Cover(full, new Vector3(0f, WorldGenerationConfig.CloudHeight + 1f, 0f), Vector3.UnitY));
        // and with the clouds turned off nothing covers anything
        Assert.Equal(0f, CloudCover.AlongRay(full, Cells, new Vector3(0f, Below, 0f), Vector3.UnitY, Vector2.Zero, 0f));
    }

    [Fact]
    public void AnOpenSkyIsUncoveredAndAnyOneLayerHidesItFully()
    {
        var eye = new Vector3(0f, Below, 0f);
        Assert.Equal(0f, Cover(Uniform(0), eye, Vector3.UnitY));
        Assert.Equal(1f, Cover(Uniform(255), eye, Vector3.UnitY));
        var oneLayer = (byte)MathF.Round(255f / WorldGenerationConfig.CloudMaxLevels);
        Assert.Equal(1f, Cover(Uniform(oneLayer), eye, Vector3.UnitY));
    }

    [Fact]
    public void ARaySlippingInThroughASideWallIsCoveredOnlyWhereTheCloudIsThatTall()
    {
        // A 45-degree ray that crosses the base and the first layer's top beside cell 1, then is
        // over cell 1 by the top of the second layer: it enters a two-layer cloud through its
        // wall, but passes clean over a one-layer cloud's top (the field stacks up to two layers).
        var thick = WorldGenerationConfig.CloudLayerThickness;
        var climb = 10f;
        var edge = CellBlocks;
        var eye = new Vector3(edge - climb - 2f * thick + 2f, WorldGenerationConfig.CloudHeight - climb, 0.5f * CellBlocks);
        var dir = Vector3.Normalize(new Vector3(1f, 1f, 0f));

        var tall = new byte[Cells * Cells];
        tall[1] = 255;
        Assert.Equal(1f, Cover(tall, eye, dir));

        var low = new byte[Cells * Cells];
        low[1] = (byte)MathF.Round(255f / WorldGenerationConfig.CloudMaxLevels);
        Assert.Equal(0f, Cover(low, eye, dir));
    }

    [Fact]
    public void CoverThinsWithDistanceLikeTheDrawnClouds()
    {
        var full = Uniform(255);
        var eye = new Vector3(0f, Below, 0f);
        var previous = 1f;
        var reachedZero = false;
        // Lower and lower rays meet the layer farther and farther away
        for (var elevation = 1f; elevation > 0.002f; elevation *= 0.95f)
        {
            var dir = Vector3.Normalize(new Vector3(MathF.Sqrt(1f - elevation * elevation), elevation, 0f));
            var cover = Cover(full, eye, dir);
            Assert.True(cover <= previous + 1e-5f, $"cover rose from {previous} to {cover} farther away");
            previous = cover;
            var distance = (WorldGenerationConfig.CloudHeight - eye.Y) / dir.Y;
            if (distance >= WorldGenerationConfig.FogFarBlocks)
            {
                Assert.Equal(0f, cover);
                reachedZero = true;
            }
        }

        Assert.True(reachedZero);
    }

    [Fact]
    public void EveryRealCloudHidesTheSkyAboveItAndEveryRealGapShowsIt()
    {
        var levels = CloudField.BuildLevels();
        var cells = levels.GetLength(0);
        var coverage = CloudField.BuildShadowCoverage(levels, WorldGenerationConfig.CloudMaxLevels);
        var cell = WorldGenerationConfig.CloudCellSize;
        for (var x = 0; x < cells; x++)
        for (var z = 0; z < cells; z++)
        {
            // straight up through the cell's center, with no drift, where the field's cell (x, z) lies
            var at = new Vector3((x + 0.5f) * cell, Below, (z + 0.5f) * cell);
            var cover = CloudCover.AlongRay(coverage, cells, at, Vector3.UnitY, Vector2.Zero, 1f);
            Assert.Equal(levels[x, z] > 0 ? 1f : 0f, cover);
        }
    }

    [Fact]
    public void ACloudEdgeDriftingAcrossTheSunCoversItGradually()
    {
        // Cell 1 of the row is cloud; slide the eye under its west edge with the sun straight up.
        // The disc's cover must rise from open to full without ever falling back, and through
        // several in-between values rather than in one flip.
        var grid = new byte[Cells * Cells];
        grid[1] = 255;
        var radius = MathUtils.ToRadians(WorldGenerationConfig.SunAngularRadiusDeg);
        var footprint = 20f * MathF.Tan(radius); // the disc's radius on the base, 20 blocks up
        var previous = 0f;
        var steps = new System.Collections.Generic.HashSet<float>();
        for (var x = CellBlocks - 2f * footprint; x <= CellBlocks + 2f * footprint; x += footprint / 20f)
        {
            var cover = CloudCover.AcrossDisc(grid, Cells, new Vector3(x, Below, 0.5f * CellBlocks), Vector3.UnitY,
                radius, Vector2.Zero, 1f);
            Assert.True(cover >= previous - 1e-6f, $"the cover fell from {previous} to {cover} as the edge advanced");
            if (cover > 0f && cover < 1f) steps.Add(cover);
            previous = cover;
        }

        Assert.Equal(1f, previous, 4);
        Assert.True(steps.Count >= 3, $"the cover jumped through only {steps.Count} in-between values");
        Assert.Equal(0f, CloudCover.AcrossDisc(new byte[Cells * Cells], Cells, new Vector3(0f, Below, 0f), Vector3.UnitY,
            radius, Vector2.Zero, 1f));
    }

    [Fact]
    public void CloudsBlockFullyAtTheDefaultOpacityAndLessAsTheyAreTurnedDown()
    {
        Assert.Equal(0f, CloudCover.Blocking(0f));
        Assert.Equal(1f, CloudCover.Blocking(WorldGenerationConfig.CloudOpacity));
        var previous = 0f;
        for (var opacity = 0f; opacity <= 1f; opacity += 0.01f)
        {
            var b = CloudCover.Blocking(opacity);
            Assert.InRange(b, 0f, 1f);
            Assert.True(b >= previous);
            previous = b;
        }
    }

    private static byte[] Uniform(byte value)
    {
        var grid = new byte[Cells * Cells];
        Array.Fill(grid, value);
        return grid;
    }

    private static float Cover(byte[] grid, Vector3 eye, Vector3 dir) =>
        CloudCover.AlongRay(grid, Cells, eye, dir, Vector2.Zero, 1f);
}
