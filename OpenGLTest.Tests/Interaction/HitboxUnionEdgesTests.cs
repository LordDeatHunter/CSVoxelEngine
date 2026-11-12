using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

public sealed class HitboxUnionEdgesTests
{
    private static List<HitboxUnionEdges.Segment> Edges(params BlockHitbox[] boxes)
    {
        var result = new List<HitboxUnionEdges.Segment>();
        HitboxUnionEdges.Build(boxes, 0f, result);
        return result;
    }

    [Fact]
    public void BoxesStackedIntoOneCubeOutlineAsOneCube()
    {
        var edges = Edges(
            new BlockHitbox(Vector3.Zero, new Vector3(1f, 0.4f, 1f)),
            new BlockHitbox(new Vector3(0f, 0.4f, 0f), Vector3.One));
        Assert.Equal(Edges(BlockHitbox.FullCube).Count, edges.Count);
        Assert.All(edges, e => Assert.False(e.From.Y > 0.01f && e.To.Y < 0.99f && e.From.Y == e.To.Y,
            "a line sits on the seam between the boxes"));
    }

    [Fact]
    public void OverlappingBoxesDropTheEdgesBuriedInTheOtherBox()
    {
        var edges = Edges(
            new BlockHitbox(Vector3.Zero, new Vector3(1f, 0.6f, 1f)),
            new BlockHitbox(new Vector3(0f, 0.3f, 0f), Vector3.One));
        Assert.Equal(12, edges.Count);
        Assert.DoesNotContain(edges, e => MathF.Abs(e.From.Y - 0.6f) < 1e-4f && MathF.Abs(e.To.Y - 0.6f) < 1e-4f);
    }

    [Fact]
    public void DisjointBoxesKeepTheirOwnEdges()
    {
        var edges = Edges(
            new BlockHitbox(Vector3.Zero, new Vector3(0.3f)),
            new BlockHitbox(new Vector3(0.6f), Vector3.One));
        Assert.Equal(24, edges.Count);
    }

    [Fact]
    public void StackedSameFootprintBoxesOutlineAsOneTallerBox()
    {
        var edges = Edges(
            new BlockHitbox(new Vector3(0.25f, 0.25f, 0.25f), new Vector3(0.75f, 0.75f, 0.75f)),
            new BlockHitbox(new Vector3(0.25f, 0.75f, 0.25f), new Vector3(0.75f, 1f, 0.75f)));
        Assert.Equal(12, edges.Count);
    }
}
