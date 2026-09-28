using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Smooth per-corner voxel light: each vertex of a full face averages the front cell with the
/// side/side/corner cells AO reads, a blocked sample standing in for the front cell. These pin
/// the rule itself; the greedy differential only proves the two mesh paths agree.
/// </summary>
public sealed class SmoothLightTests
{
    public SmoothLightTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    private static IEnumerable<Vertex[]> Quads(List<Vertex> vertices, List<ushort> indices)
    {
        for (var q = 0; q < indices.Count; q += 6)
        {
            var o = indices[q];
            yield return [vertices[o], vertices[o + 1], vertices[o + 2], vertices[o + 3]];
        }
    }

    /// <summary>The per-quad top (+Y) face of the given cell, found by its centre.</summary>
    private static Vertex[] TopFace(ChunkMeshData mesh, int x, int y, int z)
    {
        var centre = new Vector3(x + 0.5f, y + 1f, z + 0.5f);
        return Quads(mesh.Vertices, mesh.Indices).Single(q =>
            (q[0].Normal & 7) == 4 &&
            Vector3.DistanceSquared(q.Aggregate(Vector3.Zero, (s, v) => s + v.Position) / 4f, centre) < 1e-6f);
    }

    private static (ChunkManager Manager, Chunk Center) FloorWorld()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);
        return (manager, center);
    }

    private static ChunkMeshData Mesh(ChunkManager manager, Chunk center)
    {
        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        return mesh;
    }

    [Fact]
    public void OccludersDoNotDarkenTheLightOfANeighbouringFace()
    {
        var (manager, center) = FloorWorld();
        center.SetBlockRaw(8, 1, 8, Block("stone"), 0);
        var mesh = Mesh(manager, center);

        var beside = TopFace(mesh, 7, 0, 8);
        var open = TopFace(mesh, 2, 0, 2);

        // The pillar is a blocked sample for two corners; it must not pull their sun down
        Assert.All(beside, v => Assert.All(open, o => Assert.Equal(o.Sun, v.Sun)));
        // ...while AO still darkens them
        Assert.True(beside.Min(v => v.PackedColor & 0xFF) < open.Min(v => v.PackedColor & 0xFF));
    }

    [Fact]
    public void LampLightFallsOffSmoothlyAcrossAFace()
    {
        var (manager, center) = FloorWorld();
        center.SetBlockRaw(8, 3, 8, Block("lamp"), 0);
        var mesh = Mesh(manager, center);

        var face = TopFace(mesh, 4, 0, 8);
        var near = face.Where(v => v.Position.X == 5f).ToList();
        var far = face.Where(v => v.Position.X == 4f).ToList();
        Assert.Equal(2, near.Count);
        Assert.Equal(2, far.Count);
        Assert.All(near, n => Assert.All(far, f => Assert.True(n.Block > f.Block, $"{n.Block} <= {f.Block}")));

        // The shared edge carries the same light on both faces meeting at it
        var next = TopFace(mesh, 5, 0, 8);
        foreach (var v in near)
        {
            var match = next.Single(o => o.Position == v.Position);
            Assert.Equal(v.Block, match.Block);
        }
    }

    [Fact]
    public void UniformLightStaysUniform()
    {
        var (manager, center) = FloorWorld();
        var mesh = Mesh(manager, center);

        var tops = Quads(mesh.Vertices, mesh.Indices)
            .Where(q => (q[0].Normal & 7) == 4 && q.All(v => v.Position.Y == 1f))
            .SelectMany(q => q).ToList();
        Assert.NotEmpty(tops);
        Assert.All(tops, v => Assert.Equal(tops[0].Sun, v.Sun));
        Assert.All(tops, v => Assert.Equal(tops[0].Block, v.Block));
    }

    /// <summary>The single +Y quad whose centroid sits over cell column (x, z) inside the
    /// slab y0 &lt; centroid.Y &lt;= y1, from either vertex list.</summary>
    private static Vertex[] TopOver(List<Vertex> vertices, List<ushort> indices, int x, int z, float y0, float y1) =>
        Quads(vertices, indices).Single(q =>
        {
            if ((q[0].Normal & 7) != 4) return false;
            var c = q.Aggregate(Vector3.Zero, (s, v) => s + v.Position) / 4f;
            return MathF.Abs(c.X - (x + 0.5f)) < 1e-4f && MathF.Abs(c.Z - (z + 0.5f)) < 1e-4f
                   && c.Y > y0 && c.Y <= y1;
        });

    [Fact]
    public void WaterSurfaceBrightensTowardALamp()
    {
        var (manager, center) = FloorWorld();
        var water = Block("water");
        for (var x = 6; x <= 10; x++)
        for (var z = 6; z <= 10; z++)
            center.SetBlockRaw(x, 1, z, water, FluidState.Source);
        // On the floor, just outside the pool's -X edge
        center.SetBlockRaw(5, 1, 8, Block("lamp"), 0);
        var mesh = Mesh(manager, center);

        // Two steps from the lamp: its X == 7 edge is the nearer one
        var face = TopOver(mesh.TranslucentVertices, mesh.TranslucentIndices, 7, 8, 1f, 2f);
        var near = face.Where(v => v.Position.X == 7f).ToList();
        var far = face.Where(v => v.Position.X == 8f).ToList();
        Assert.Equal(2, near.Count);
        Assert.Equal(2, far.Count);
        Assert.All(near, n => Assert.All(far, f => Assert.True(n.Block > f.Block, $"{n.Block} <= {f.Block}")));

        // The neighbouring pool cell computes the same light at the corners the two share
        var next = TopOver(mesh.TranslucentVertices, mesh.TranslucentIndices, 8, 8, 1f, 2f);
        foreach (var v in far)
        {
            var match = next.Single(o => o.Position.X == v.Position.X && o.Position.Z == v.Position.Z);
            Assert.Equal(v.Block, match.Block);
        }
    }

    [Fact]
    public void SnowLayerLightIsContinuousWithTheFullBlockBesideIt()
    {
        var (manager, center) = FloorWorld();
        center.SetBlockRaw(8, 1, 8, Block("stone"), 0);
        center.SetBlockRaw(7, 1, 8, Block("snow"), 4);
        // Hanging over the floor, so the y = 2 row around the snow is open and lit unevenly
        center.SetBlockRaw(3, 3, 8, Block("lamp"), 0);
        var mesh = Mesh(manager, center);

        var snow = TopOver(mesh.Vertices, mesh.Indices, 7, 8, 1f, 1.99f);
        var stone = TopFace(mesh, 8, 1, 8);

        // Both faces sample the same four y = 2 cells at their shared X == 8 corners
        var shared = snow.Where(v => v.Position.X == 8f).ToList();
        Assert.Equal(2, shared.Count);
        foreach (var v in shared)
        {
            var match = stone.Single(o => o.Position.X == v.Position.X && o.Position.Z == v.Position.Z);
            Assert.Equal(match.Block, v.Block);
        }

        // ...and the light across the snow top is not flat
        var nearer = snow.Where(v => v.Position.X == 7f).ToList();
        Assert.Equal(2, nearer.Count);
        Assert.All(nearer, n => Assert.All(shared, s => Assert.NotEqual(s.Block, n.Block)));
    }
}
