using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The content-free geometry of <see cref="TreeWoodShape"/>: node width by thickness, the tube
/// box toward each direction and the direction pairing the connection rule relies on.
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
}
