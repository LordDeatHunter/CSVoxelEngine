using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>The leaf cells an axe hit shakes loose (<see cref="TreeShudder"/>).</summary>
public sealed class TreeShudderTests
{
    private static readonly Vector3 Wood = new(10f, 5f, 10f);

    [Fact]
    public void OnlyLeavesInsideTheBoxAboveAndAroundTheHitAreGathered()
    {
        var cells = new List<Vector3>();
        // A crown above and a leaf far off to the side and one below the hit
        bool IsLeaf(int x, int y, int z) =>
            (y is >= 8 and <= 10 && x is >= 9 and <= 11 && z is >= 9 and <= 11) || (x == 40 && y == 8) || (y == 3);

        TreeShudder.Gather(Wood, IsLeaf, cells, maxCells: 1000, reach: 4, height: 14);

        Assert.NotEmpty(cells);
        foreach (var cell in cells)
        {
            Assert.InRange(cell.Y, Wood.Y, Wood.Y + 14);
            Assert.InRange(MathF.Abs(cell.X - Wood.X), 0, 4);
            Assert.InRange(MathF.Abs(cell.Z - Wood.Z), 0, 4);
        }
    }

    [Fact]
    public void ADenseCrownIsThinnedToTheCapAndSpreadThroughTheBox()
    {
        var cells = new List<Vector3>();
        TreeShudder.Gather(Wood, (_, y, _) => y >= 8, cells, maxCells: 5, reach: 4, height: 14);

        Assert.Equal(5, cells.Count);
        Assert.Equal(cells.Count, new HashSet<Vector3>(cells).Count);
        // Not all from the first rows of the scan: the picks reach different heights
        Assert.True(cells[^1].Y > cells[0].Y);
    }

    [Fact]
    public void NoLeavesOrNoCapGathersNothing()
    {
        var cells = new List<Vector3> { Vector3.One };
        TreeShudder.Gather(Wood, (_, _, _) => false, cells, maxCells: 5, reach: 4, height: 14);
        Assert.Empty(cells);

        TreeShudder.Gather(Wood, (_, _, _) => true, cells, maxCells: 0, reach: 4, height: 14);
        Assert.Empty(cells);
    }
}
