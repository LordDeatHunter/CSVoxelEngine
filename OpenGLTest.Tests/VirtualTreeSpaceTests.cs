using Xunit;

namespace silkgltriangle.Tests;

public sealed class VirtualTreeSpaceTests
{
    public VirtualTreeSpaceTests() => TestContent.EnsureLoaded();

    private static int Slope(int x, int z) => x - z / 2;

    [Fact]
    public void FreeExactlyAboveTheHeightAndNotOccupied()
    {
        var space = new VirtualTreeSpace(Slope);
        space.Occupy(3, Slope(3, 1) + 2, 1);

        for (var x = -4; x <= 4; x++)
        for (var z = -3; z <= 3; z++)
        {
            Assert.Equal(Slope(x, z), space.GroundHeight(x, z));
            for (var y = Slope(x, z) - 3; y <= Slope(x, z) + 3; y++)
                Assert.Equal(y > Slope(x, z) && !space.IsOccupied(x, y, z), space.IsFree(x, y, z));
        }
    }

    [Fact]
    public void OccupyAffectsOnlyThatCell()
    {
        var space = new VirtualTreeSpace((_, _) => 0);
        const int cx = 5, cy = 7, cz = -2;
        Assert.True(space.IsFree(cx, cy, cz));

        space.Occupy(cx, cy, cz);
        space.Occupy(cx, cy, cz);

        Assert.Equal(1, space.OccupiedCount);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var self = dx == 0 && dy == 0 && dz == 0;
            Assert.Equal(self, space.IsOccupied(cx + dx, cy + dy, cz + dz));
            Assert.Equal(!self, space.IsFree(cx + dx, cy + dy, cz + dz));
        }
    }

    [Fact]
    public void OccupyAllMatchesPerCellOccupy()
    {
        var cells = new[]
        {
            new TreeCell(2, Slope(2, 1) + 1, 1, 1, 0),
            new TreeCell(-3, Slope(-3, 2) + 4, 2, 1, 0),
            new TreeCell(2, Slope(2, 1) + 1, 1, 2, 5), // the first cell again, other block and state
            new TreeCell(0, Slope(0, -1) + 2, -1, 1, 0),
        };

        var bulk = new VirtualTreeSpace(Slope);
        bulk.OccupyAll(cells);

        var single = new VirtualTreeSpace(Slope);
        foreach (var c in cells)
            single.Occupy(c.X, c.Y, c.Z);

        Assert.Equal(single.OccupiedCount, bulk.OccupiedCount);
        Assert.Equal(cells.Length - 1, bulk.OccupiedCount);

        for (var x = -4; x <= 4; x++)
        for (var z = -3; z <= 3; z++)
        for (var y = Slope(x, z) - 1; y <= Slope(x, z) + 5; y++)
        {
            Assert.Equal(single.IsOccupied(x, y, z), bulk.IsOccupied(x, y, z));
            Assert.Equal(single.IsFree(x, y, z), bulk.IsFree(x, y, z));
        }
    }

    [Fact]
    public void CarvedCellsBelowTheSurfaceAreFree()
    {
        const int height = 10;
        static bool Carved(int x, int y, int z) => x == 0 && z == 0 && y is 8 or 9;
        var space = new VirtualTreeSpace((_, _) => height, Carved);

        for (var y = height - 5; y <= height; y++)
        {
            Assert.Equal(Carved(0, y, 0), space.IsFree(0, y, 0));
            Assert.False(space.IsFree(1, y, 0));
        }

        space.Occupy(0, 9, 0);
        Assert.False(space.IsFree(0, 9, 0));
    }

    [Fact]
    public void FromTerrainAgreesWithTheColumnHeights()
    {
        var space = VirtualTreeSpace.FromTerrain();
        foreach (var x in new[] { -17, -1, 0, 15, 16 })
        foreach (var z in new[] { -1, 0, 16 })
        {
            var expected = ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
                [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
            Assert.Equal(expected, space.GroundHeight(x, z));
            Assert.False(space.IsFree(x, expected, z));
            Assert.True(space.IsFree(x, expected + 1, z));
        }
    }
}
