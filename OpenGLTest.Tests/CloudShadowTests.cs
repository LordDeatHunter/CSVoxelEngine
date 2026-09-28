using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The GL-free half of the cloud shadow: the coverage bake that turns the mesh's level grid into the
/// shadow map, and the world-to-field UV mapping the fragment shader mirrors. The upload and the
/// sampling itself need a live GL context and are only checked by running the game.
/// </summary>
public sealed class CloudShadowTests
{
    [Fact]
    public void CoverageFollowsTheMeshLevels()
    {
        var levels = CloudField.BuildLevels();
        var maxLevels = WorldGenerationConfig.CloudMaxLevels;
        var coverage = CloudField.BuildShadowCoverage(levels, maxLevels);
        var cells = levels.GetLength(0);
        Assert.Equal(cells * levels.GetLength(1), coverage.Length);

        // Each level must map to one coverage value, and a thicker cell must shade strictly more.
        var byLevel = new int[maxLevels + 1];
        Array.Fill(byLevel, -1);
        var seenMax = false;
        for (var x = 0; x < cells; x++)
        for (var z = 0; z < levels.GetLength(1); z++)
        {
            var level = levels[x, z];
            var c = coverage[z * cells + x];
            Assert.Equal(level == 0, c == 0);
            if (byLevel[level] < 0) byLevel[level] = c;
            else Assert.Equal(byLevel[level], c);
            seenMax |= level == maxLevels;
        }

        Assert.True(seenMax, "the configured field never reaches the maximum level");
        var previous = -1;
        for (var l = 0; l <= maxLevels; l++)
        {
            if (byLevel[l] < 0) continue;
            Assert.True(byLevel[l] > previous, $"level {l} does not shade more than a thinner cell");
            previous = byLevel[l];
        }

        Assert.Equal(255, byLevel[maxLevels]);
    }

    [Fact]
    public void FieldUvLandsOnTheDrawnCopies()
    {
        var field = WorldGenerationConfig.CloudFieldSize;
        var cellSize = WorldGenerationConfig.CloudCellSize;
        var cells = CloudField.Cells;
        const float tolerance = 1e-4f;

        foreach (var time in new[] { 0f, 7.25f, 333.3f, 1200f, 5000f })
        {
            var drift = CloudCopies.Drift(time, field);
            for (var s = -3; s <= 3; s++)
            for (var k = -1; k <= 1; k++)
            {
                var snap = s * field;
                var originX = snap + k * field + drift;
                var originZ = snap + k * field;

                var uv = CloudCopies.FieldUv(originX, originZ, drift, field);
                Assert.True(DistanceToInteger(uv.X) < tolerance, $"copy origin u {uv.X} at drift {drift}");
                Assert.True(DistanceToInteger(uv.Y) < tolerance, $"copy origin v {uv.Y} at drift {drift}");

                foreach (var (i, j) in new[] { (0, 0), (1, 0), (0, 1), (17, 93), (cells - 1, cells / 2) })
                {
                    var cell = CloudCopies.FieldUv(originX + cellSize * i, originZ + cellSize * j, drift, field);
                    Assert.True(WrappedDistance(cell.X, (float)i / cells) < tolerance,
                        $"cell {i} lands at u {cell.X}, drift {drift}");
                    Assert.True(WrappedDistance(cell.Y, (float)j / cells) < tolerance,
                        $"cell {j} lands at v {cell.Y}, drift {drift}");
                }
            }
        }
    }

    private static float DistanceToInteger(float v) => MathF.Abs(v - MathF.Round(v));

    // Distance between two UVs on the unit circle the texture's REPEAT wrap folds them onto.
    private static float WrappedDistance(float a, float b)
    {
        var d = (a - b) - MathF.Floor(a - b);
        return MathF.Min(d, 1f - d);
    }
}
