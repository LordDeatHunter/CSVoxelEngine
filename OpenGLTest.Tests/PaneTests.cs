using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A glass pane is a thin translucent block on glass's path: it never hides a neighbour's face,
/// lets light through and is drawn in the blended pass, fused with the panes beside it.
/// </summary>
public sealed class PaneTests
{
    private const int FloorY = 4;

    private readonly byte _pane;
    private readonly byte _stone;

    public PaneTests()
    {
        TestContent.EnsureLoaded();
        _pane = ContentRegistry.RequireBlock("glass_pane").Id;
        _stone = ContentRegistry.RequireBlock("stone").Id;
    }

    private static IEnumerable<Vertex[]> Quads(List<Vertex> vertices)
    {
        for (var q = 0; q + 3 < vertices.Count; q += 4)
            yield return [vertices[q], vertices[q + 1], vertices[q + 2], vertices[q + 3]];
    }

    // Whether some quad of the list lies flat on the plane x = planeX inside the cell column
    // (y, z), facing along X
    private static bool HasXFace(List<Vertex> vertices, float planeX, int y, int z) =>
        Quads(vertices).Any(q => q.All(v => MathF.Abs(v.Position.X - planeX) < 1e-4f)
                                 && q.Average(v => v.Position.Y) is var cy && cy > y && cy < y + 1
                                 && q.Average(v => v.Position.Z) is var cz && cz > z && cz < z + 1);

    [Fact]
    public void APaneNeverHidesANeighbourAndIsDrawnInTheBlendedPass()
    {
        var type = BlockType.ById[_pane]!;
        Assert.False(BlockType.Occludes(_pane));
        Assert.False(BlockType.OccludesAO(_pane));
        Assert.True(BlockType.PassesLightById[_pane]);
        Assert.True(type.Translucent);

        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(8, FloorY, 8, _stone, 0);
        center.SetBlockRaw(9, FloorY, 8, _pane, 0);
        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);

        // The stone's face toward the pane still draws, and the pane draws only blended
        Assert.True(HasXFace(mesh!.Vertices, 9f, FloorY, 8), "the stone face behind the pane was culled");
        Assert.NotEmpty(mesh.TranslucentVertices);
        Assert.DoesNotContain(mesh.Vertices, v => v.Layer == type.Side);
    }

    [Fact]
    public void PanesInARowShowNoFaceBetweenThem()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(8, FloorY, 8, _pane, 0);
        center.SetBlockRaw(9, FloorY, 8, _pane, 0);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);

        // The outer ends of the row draw, the shared one does not
        Assert.True(HasXFace(mesh!.TranslucentVertices, 8f, FloorY, 8));
        Assert.True(HasXFace(mesh.TranslucentVertices, 10f, FloorY, 8));
        Assert.False(HasXFace(mesh.TranslucentVertices, 9f, FloorY, 8), "two panes in a row draw a face between them");
    }

    // Block light just under the middle of a stone floor spanning the chunk, with a lamp right
    // above the middle cell, which holds either a pane or more stone
    private float LightUnderTheMiddle(byte middle)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var origin = center.Position * Chunk.Size;
        const int floorY = 8;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, floorY, z, x == 8 && z == 8 ? middle : _stone, 0);
        center.SetBlockRaw(8, floorY + 1, 8, ContentRegistry.RequireBlock("lamp").Id, 0);
        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        center.PublishLight(mesh!.Light);
        return EntityLight.Sample(manager, origin + new Vector3(8.5f, floorY - 0.5f, 8.5f)).Block;
    }

    [Fact]
    public void LampLightShinesThroughAPaneInAFloor()
    {
        var throughPane = LightUnderTheMiddle(_pane);
        var throughStone = LightUnderTheMiddle(_stone);
        Assert.True(throughPane > throughStone, $"through the pane {throughPane}, through stone {throughStone}");
    }
}
