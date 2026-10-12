using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

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

    /// <summary>The per-quad top (+Y) face of the given cell, found by its center.</summary>
    private static Vertex[] TopFace(ChunkMeshData mesh, int x, int y, int z)
    {
        var center = new Vector3(x + 0.5f, y + 1f, z + 0.5f);
        return Quads(mesh.Vertices, mesh.Indices).Single(q =>
            (q[0].Normal & 7) == 4 &&
            Vector3.DistanceSquared(q.Aggregate(Vector3.Zero, (s, v) => s + v.Position) / 4f, center) < 1e-6f);
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
    public void OccludersDoNotDarkenTheLightOfANeighboringFace()
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

        // The neighboring pool cell computes the same light at the corners the two share
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

    // ---------------------------------------------------------------- colored lamp light

    private static Vector3 Decode(Vertex v) => LightTint.Decode(v.BlockTint, BlockLightPalette.Linear);

    private static Vector3 Color(byte id) => BlockLightPalette.Linear[BlockType.LightSlot[id]];

    /// <summary>The colored proof lamp, with the preconditions the tests below lean on.</summary>
    private static (byte Lamp, byte Blue) Lamps()
    {
        var lamp = Block("lamp");
        var blue = Block("blue_lamp");
        Assert.Equal(BlockType.Emission[lamp], BlockType.Emission[blue]);
        Assert.NotEqual(BlockType.LightSlot[lamp], BlockType.LightSlot[blue]);
        Assert.NotEqual(Color(lamp), Color(blue));
        return (lamp, blue);
    }

    /// <summary>Vertices of +Y faces lying in the floor top (Y == 1) on the Z == <paramref name="z"/>
    /// corner line, grouped by X.</summary>
    private static ILookup<float, Vertex> FloorRow(ChunkMeshData mesh, int z) =>
        Quads(mesh.Vertices, mesh.Indices)
            .Where(q => (q[0].Normal & 7) == 4 && q.All(v => v.Position.Y == 1f))
            .SelectMany(q => q)
            .Where(v => v.Position.Z == z)
            .ToLookup(v => v.Position.X);

    [Fact]
    public void AColoredLampLightsExactlyAsBrightlyAsTheDefaultLamp()
    {
        var (lamp, blue) = Lamps();
        var (manager, center) = FloorWorld();
        center.SetBlockRaw(8, 3, 8, lamp, 0);
        var warm = Mesh(manager, center);
        center.SetBlockRaw(8, 3, 8, blue, 0);
        var cool = Mesh(manager, center);

        Assert.Equal(warm.Vertices.Count, cool.Vertices.Count);
        Assert.Contains(warm.Vertices, v => v.Block > 0);
        for (var i = 0; i < warm.Vertices.Count; i++)
        {
            var w = warm.Vertices[i];
            var c = cool.Vertices[i];
            Assert.Equal(w.Position, c.Position);
            Assert.Equal(w.Sun, c.Sun);
            Assert.Equal(w.Block, c.Block);
            if (w.Block > 0) Assert.NotEqual(Decode(w), Decode(c));
        }
    }

    [Fact]
    public void DefaultLampsBakeTheDefaultTint()
    {
        var (lamp, _) = Lamps();
        var (manager, center) = FloorWorld();
        center.SetBlockRaw(4, 3, 4, lamp, 0);
        center.SetBlockRaw(11, 2, 10, lamp, 0);
        foreach (var greedy in new[] { false, true })
        {
            var mesh = center.GenerateMeshData(manager, greedy);
            Assert.NotNull(mesh);
            Assert.Contains(mesh.Vertices, v => v.Block > 0);
            Assert.All(mesh.Vertices, v => Assert.Equal(0, v.BlockTint));
        }
    }

    [Fact]
    public void TintShiftsTowardTheNearerLampColor()
    {
        var (lamp, blue) = Lamps();
        var (manager, center) = FloorWorld();
        center.SetBlockRaw(3, 3, 8, lamp, 0);
        center.SetBlockRaw(12, 3, 8, blue, 0);
        var row = FloorRow(Mesh(manager, center), 8);
        var target = Color(blue);

        float Distance(Vertex v) => Vector3.Distance(Decode(v), target);
        var previous = float.MaxValue;
        for (var x = 3; x <= 13; x++)
        {
            var here = row[x].ToList();
            Assert.NotEmpty(here);
            Assert.All(here, v => Assert.True(Distance(v) <= previous + 1e-5f,
                $"x {x}: tint {Decode(v)} moved away from the colored lamp ({Distance(v)} > {previous})"));
            previous = here.Max(Distance);
        }

        // Anti-vacuity: the row really goes from one color to the other
        Assert.True(row[3].Min(Distance) > row[13].Max(Distance) + 1e-3f);
    }

    [Fact]
    public void ColorTiesDoNotDependOnPlacementOrder()
    {
        var (lamp, blue) = Lamps();
        var lower = Math.Min(BlockType.LightSlot[lamp], BlockType.LightSlot[blue]);

        // A default and a colored lamp equidistant from column x = 8, then the same with the two
        // swapped: world B is world A mirrored about that column, but the scan that seeds the
        // flood meets the two lamps in the opposite order
        ChunkMeshData Build(byte left, byte right)
        {
            var (manager, center) = FloorWorld();
            center.SetBlockRaw(5, 3, 8, left, 0);
            center.SetBlockRaw(11, 3, 8, right, 0);
            return Mesh(manager, center);
        }
        var a = Build(lamp, blue);
        var b = Build(blue, lamp);

        // The published light snapshot carries each cell's slot in its second half
        const int count = Chunk.Size * Chunk.Size * Chunk.Size;
        Assert.NotNull(a.Light);
        Assert.NotNull(b.Light);
        Assert.Equal(2 * count, a.Light!.Length);
        Assert.Equal(2 * count, b.Light!.Length);
        int Slot(byte[] light, int x) => light[count + Chunk.Idx(x, 1, 8)];

        // The equidistant cell takes the lower slot both ways, and every cell mirrors
        Assert.Equal(lower, Slot(a.Light, 8));
        Assert.Equal(lower, Slot(b.Light, 8));
        for (var x = 5; x <= 11; x++)
            Assert.Equal(Slot(a.Light, x), Slot(b.Light, 16 - x));
        Assert.NotEqual(Slot(a.Light, 5), Slot(a.Light, 11));

        // So does the baked tint: corner X samples cells X - 1 and X, the mirrored corner
        // 17 - X the mirrored cells
        var rowA = FloorRow(a, 8);
        var rowB = FloorRow(b, 8);
        for (var x = 5; x <= 12; x++)
        {
            var expected = rowA[x].Select(v => v.BlockTint).Distinct().Single();
            Assert.All(rowB[17 - x], v => Assert.Equal(expected, v.BlockTint));
        }
    }

    [Fact]
    public void EmittersNearAColoredLampTakeItsTint()
    {
        var (lamp, blue) = Lamps();
        var water = Block("water");
        var snow = Block("snow");

        // A snow layer and a pool beside the lamp under test; a warm lamp across the pool mixes
        // a second color into the far cells, so the emitters resolve real two-slot corners
        (ChunkManager, Chunk) Build(byte near)
        {
            var (manager, center) = FloorWorld();
            for (var x = 7; x <= 10; x++)
            for (var z = 6; z <= 10; z++)
                center.SetBlockRaw(x, 1, z, water, FluidState.Source);
            center.SetBlockRaw(6, 1, 8, snow, 4);
            center.SetBlockRaw(5, 1, 8, near, 0);
            center.SetBlockRaw(12, 1, 8, lamp, 0);
            return (manager, center);
        }

        var (bm, bc) = Build(blue);
        var mesh = Mesh(bm, bc);
        bool NearerBlue(Vertex v) =>
            Vector3.Distance(Decode(v), Color(blue)) < Vector3.Distance(Decode(v), Color(lamp));

        var snowTop = TopOver(mesh.Vertices, mesh.Indices, 6, 8, 1f, 1.99f);
        Assert.All(snowTop, v => Assert.True(v.Block > 0 && NearerBlue(v), $"snow tint {v.BlockTint}"));
        var waterTop = TopOver(mesh.TranslucentVertices, mesh.TranslucentIndices, 7, 8, 1f, 2f);
        Assert.All(waterTop, v => Assert.True(v.Block > 0 && NearerBlue(v), $"water tint {v.BlockTint}"));
        // The warm lamp really does reach the pool, so the far side mixes toward it
        var farWater = TopOver(mesh.TranslucentVertices, mesh.TranslucentIndices, 10, 8, 1f, 2f);
        Assert.Contains(farWater, v => !NearerBlue(v) || (v.BlockTint >> 6) > 0);

        var (wm, wc) = Build(lamp);
        var warm = Mesh(wm, wc);
        Assert.Contains(warm.TranslucentVertices, v => v.Block > 0);
        Assert.All(warm.Vertices, v => Assert.Equal(0, v.BlockTint));
        Assert.All(warm.TranslucentVertices, v => Assert.Equal(0, v.BlockTint));
    }
}
