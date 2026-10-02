using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The content-free geometry of <see cref="TreeWoodShape"/>: node width by thickness, the tube
/// box toward each direction, the direction pairing the connection rule relies on, and the
/// pixel-rim bevel: the stair-stepped rim boxes of each corner mask and the side-fullness
/// predicate neighbours cull against.
/// </summary>
public sealed class TreeWoodShapeGeometryTests
{
    private const float Eps = 1e-6f;

    [Fact]
    public void HalfWidthGrowsWithThicknessAndFillsTheCellAtAFullCube()
    {
        for (var t = 1; t <= WoodState.MaxThickness; t++)
            Assert.True(TreeWoodShape.HalfWidth(t) > TreeWoodShape.HalfWidth(t - 1));
        Assert.True(TreeWoodShape.HalfWidth(0) > 0f);
        Assert.Equal(0.5f, TreeWoodShape.HalfWidth(WoodState.FullCube), 6);
    }

    [Fact]
    public void OppositeIsAnInvolutionThatFlipsTheNormal()
    {
        for (var d = 0; d < 6; d++)
        {
            var o = TreeWoodShape.Opposite(d);
            Assert.NotEqual(d, o);
            Assert.Equal(d, TreeWoodShape.Opposite(o));
            Assert.Equal(Vector3.Zero, Chunk.CubeFaces[d].normal + Chunk.CubeFaces[o].normal);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 2)]
    [InlineData(9, 9)]
    [InlineData(14, 3)]
    public void TubeBoxRunsFromTheNodeFaceToTheCellFaceAndIsCentredAcross(int nodeThickness, int tubeThickness)
    {
        var nodeHalf = TreeWoodShape.HalfWidth(nodeThickness);
        var tubeHalf = TreeWoodShape.HalfWidth(tubeThickness);
        for (var dir = 0; dir < 6; dir++)
        {
            TreeWoodShape.TubeBox(dir, nodeHalf, tubeHalf, out var min, out var max);
            var normal = Chunk.CubeFaces[dir].normal;
            for (var axis = 0; axis < 3; axis++)
            {
                var n = normal[axis];
                if (n > 0f)
                {
                    Assert.Equal(0.5f + nodeHalf, min[axis], Eps);
                    Assert.Equal(1f, max[axis], Eps);
                }
                else if (n < 0f)
                {
                    Assert.Equal(0f, min[axis], Eps);
                    Assert.Equal(0.5f - nodeHalf, max[axis], Eps);
                }
                else
                {
                    // Across the tube: symmetric about the cell centre, tubeHalf either side
                    Assert.Equal(1f, min[axis] + max[axis], Eps);
                    Assert.Equal(2f * tubeHalf, max[axis] - min[axis], Eps);
                }
            }
        }
    }

    [Fact]
    public void TubeBoxTouchesTheNodeBoxFaceItLeaves()
    {
        var half = TreeWoodShape.HalfWidth(6);
        TreeWoodShape.NodeBox(half, out var nodeMin, out var nodeMax);
        for (var dir = 0; dir < 6; dir++)
        {
            TreeWoodShape.TubeBox(dir, half, half, out var min, out var max);
            var normal = Chunk.CubeFaces[dir].normal;
            for (var axis = 0; axis < 3; axis++)
            {
                if (normal[axis] > 0f) Assert.Equal(nodeMax[axis], min[axis], Eps);
                else if (normal[axis] < 0f) Assert.Equal(nodeMin[axis], max[axis], Eps);
                else
                {
                    Assert.Equal(nodeMin[axis], min[axis], Eps);
                    Assert.Equal(nodeMax[axis], max[axis], Eps);
                }
            }
        }
    }

    private const int Px = WoodState.PixelsPerBlock;

    // The four rim corners as (bit, -X side?, -Z side?)
    private static readonly (int Bit, bool MinX, bool MinZ)[] Corners =
    [
        (TreeWoodShape.CornerMinXMinZ, true, true),
        (TreeWoodShape.CornerMaxXMinZ, false, true),
        (TreeWoodShape.CornerMinXMaxZ, true, false),
        (TreeWoodShape.CornerMaxXMaxZ, false, false),
    ];

    private static BlockHitbox[] Boxes(int mask)
    {
        var dst = new BlockHitbox[TreeWoodShape.MaxRimBoxes];
        var n = TreeWoodShape.RimBoxes(mask, dst);
        return dst[..n];
    }

    // How many boxes cover each pixel column centre [x, z] of the cell's 16x16 footprint
    private static int[,] Coverage(int mask)
    {
        var cover = new int[Px, Px];
        foreach (var b in Boxes(mask))
            for (var x = 0; x < Px; x++)
            for (var z = 0; z < Px; z++)
            {
                var cx = (x + 0.5f) / Px;
                var cz = (z + 0.5f) / Px;
                if (cx > b.Min.X && cx < b.Max.X && cz > b.Min.Z && cz < b.Max.Z) cover[x, z]++;
            }

        return cover;
    }

    // Whether the pixel [x, z] lies in corner c's carved region: within the inset of the row
    // counted from the corner's Z edge, measured from the corner's X edge
    private static bool InCarvedRegion((int Bit, bool MinX, bool MinZ) c, int x, int z)
    {
        var row = c.MinZ ? z : Px - 1 - z;
        var col = c.MinX ? x : Px - 1 - x;
        return col < TreeWoodShape.RimInsetPx(row);
    }

    [Fact]
    public void RimBoxesTileTheCarvedFootprint()
    {
        for (var mask = 0; mask < 16; mask++)
        {
            var boxes = Boxes(mask);
            Assert.NotEmpty(boxes);
            foreach (var b in boxes)
            {
                Assert.Equal(0f, b.Min.Y, Eps);
                Assert.Equal(1f, b.Max.Y, Eps);
                Assert.True(b.Min.X >= 0f && b.Min.Z >= 0f && b.Max.X <= 1f && b.Max.Z <= 1f, $"mask {mask}: {b}");
                Assert.True(b.Max.X > b.Min.X && b.Max.Z > b.Min.Z, $"mask {mask}: empty box {b}");
            }

            var cover = Coverage(mask);
            for (var x = 0; x < Px; x++)
            for (var z = 0; z < Px; z++)
            {
                Assert.True(cover[x, z] <= 1, $"mask {mask}: boxes overlap at {x},{z}");
                var carved = Corners.Any(c => (mask & c.Bit) != 0 && InCarvedRegion(c, x, z));
                Assert.Equal(!carved, cover[x, z] == 1);

                // A pixel in the quadrant of a corner that is not carved is always material
                var quadrant = Corners.Single(c => c.MinX == x < Px / 2 && c.MinZ == z < Px / 2);
                if ((mask & quadrant.Bit) == 0) Assert.Equal(1, cover[x, z]);
            }
        }
    }

    [Fact]
    public void CarvingMoreCornersNeverAddsMaterial()
    {
        for (var a = 0; a < 16; a++)
        for (var b = 0; b < 16; b++)
        {
            if ((a & b) != a) continue;
            var fewer = Coverage(a);
            var more = Coverage(b);
            for (var x = 0; x < Px; x++)
            for (var z = 0; z < Px; z++)
                Assert.True(more[x, z] <= fewer[x, z], $"mask {b} covers {x},{z} that its subset {a} leaves open");
        }

        // And carving anything takes something away
        for (var mask = 1; mask < 16; mask++)
            Assert.Contains(0, Coverage(mask).Cast<int>());
    }

    [Fact]
    public void RimInsetsShrinkAwayFromTheCorner()
    {
        const int r = WorldGenerationConfig.TreeRimBevelPx;
        Assert.True(TreeWoodShape.RimInsetPx(0) > 0);
        for (var row = 0; row < Px; row++)
        {
            var inset = TreeWoodShape.RimInsetPx(row);
            Assert.InRange(inset, 0, r);
            if (row + 1 < Px) Assert.True(TreeWoodShape.RimInsetPx(row + 1) <= inset, $"row {row + 1} cuts deeper than {row}");
            if (row >= r) Assert.Equal(0, inset);
        }
    }

    [Fact]
    public void MirroringTheMaskMirrorsTheBoxes()
    {
        static int Swap(int m, int a, int b) =>
            (m & ~(a | b)) | ((m & a) != 0 ? b : 0) | ((m & b) != 0 ? a : 0);

        for (var mask = 0; mask < 16; mask++)
        {
            var mirrorX = Swap(Swap(mask, TreeWoodShape.CornerMinXMinZ, TreeWoodShape.CornerMaxXMinZ),
                TreeWoodShape.CornerMinXMaxZ, TreeWoodShape.CornerMaxXMaxZ);
            var mirrorZ = Swap(Swap(mask, TreeWoodShape.CornerMinXMinZ, TreeWoodShape.CornerMinXMaxZ),
                TreeWoodShape.CornerMaxXMinZ, TreeWoodShape.CornerMaxXMaxZ);
            var cover = Coverage(mask);
            var coverX = Coverage(mirrorX);
            var coverZ = Coverage(mirrorZ);
            for (var x = 0; x < Px; x++)
            for (var z = 0; z < Px; z++)
            {
                Assert.Equal(cover[x, z], coverX[Px - 1 - x, z]);
                Assert.Equal(cover[x, z], coverZ[x, Px - 1 - z]);
            }
        }
    }

    [Fact]
    public void RimBoxCountFitsTheBound()
    {
        for (var mask = 0; mask < 16; mask++)
        {
            var dst = new BlockHitbox[TreeWoodShape.MaxRimBoxes];
            var n = TreeWoodShape.RimBoxes(mask, dst);
            Assert.InRange(n, 1, TreeWoodShape.MaxRimBoxes);
        }

        // An uncarved cell is exactly the whole cube
        Assert.Equal(new[] { BlockHitbox.FullCube }, Boxes(0));
    }

    [Fact]
    public void RimSideFullMatchesCoverage()
    {
        for (var mask = 0; mask < 16; mask++)
        {
            var cover = Coverage(mask);
            var all = Enumerable.Range(0, Px * Px).All(i => cover[i % Px, i / Px] == 1);
            for (var dir = 0; dir < 6; dir++)
            {
                var complete = dir switch
                {
                    0 => Enumerable.Range(0, Px).All(x => cover[x, 0] == 1),
                    1 => Enumerable.Range(0, Px).All(x => cover[x, Px - 1] == 1),
                    2 => Enumerable.Range(0, Px).All(z => cover[0, z] == 1),
                    3 => Enumerable.Range(0, Px).All(z => cover[Px - 1, z] == 1),
                    _ => all,
                };
                Assert.Equal(complete, TreeWoodShape.RimSideFull(mask, dir));
            }
        }
    }
}
