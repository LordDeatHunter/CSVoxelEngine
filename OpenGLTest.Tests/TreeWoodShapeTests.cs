using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The tree-wood connection rule (<see cref="TreeWoodShape.Connects"/>) and the shape built on
/// it: connections are symmetric, detached wood and foreign blocks never join, and the hitboxes
/// <see cref="TreeWoodShape.GetBoxes"/> returns are exactly the geometry the mesher draws.
/// </summary>
public sealed class TreeWoodShapeTests
{
    public TreeWoodShapeTests() => TestContent.EnsureLoaded();

    private static byte Oak => ContentRegistry.RequireBlock("oak_log").Id;
    private static byte Spruce => ContentRegistry.RequireBlock("spruce_log").Id;

    private const float Eps = 1e-5f;

    private static readonly int[] Thicknesses = [0, 3, 7, 11, 14, WoodState.FullCube];

    // Every parent code (the six directions, root and detached) at several thicknesses
    private static IEnumerable<byte> SweepStates()
    {
        foreach (var t in Thicknesses)
            for (var parent = 0; parent <= WoodState.ParentDetached; parent++)
                yield return WoodState.Encode(t, parent, false);
    }

    private static Vector3 Offset(int dir) => Chunk.CubeFaces[dir].normal;

    [Fact]
    public void ConnectionIsSymmetric()
    {
        var oak = Oak;
        var states = SweepStates().ToArray();
        for (var dir = 0; dir < 6; dir++)
        foreach (var a in states)
        foreach (var b in states)
            Assert.Equal(TreeWoodShape.Connects(oak, a, oak, b, dir),
                TreeWoodShape.Connects(oak, b, oak, a, TreeWoodShape.Opposite(dir)));

        // A cell's mask bit toward a neighbour equals that neighbour's mask bit back toward it
        Span<byte> ids = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        Span<byte> backIds = stackalloc byte[6];
        Span<byte> backStates = stackalloc byte[6];
        var seed = 12345u;
        for (var trial = 0; trial < 500; trial++)
        {
            var centre = states[(int)(Next(ref seed) % (uint)states.Length)];
            for (var dir = 0; dir < 6; dir++)
            {
                // Mostly oak, sometimes air or spruce, so foreign neighbours are in the sweep too
                var pick = Next(ref seed) % 8;
                ids[dir] = pick == 0 ? (byte)0 : pick == 1 ? Spruce : oak;
                nbStates[dir] = states[(int)(Next(ref seed) % (uint)states.Length)];
            }

            var mask = TreeWoodShape.ConnectionMask(oak, centre, ids, nbStates);
            for (var dir = 0; dir < 6; dir++)
            {
                backIds.Clear();
                backStates.Clear();
                var back = TreeWoodShape.Opposite(dir);
                backIds[back] = oak;
                backStates[back] = centre;
                var neighbourMask = TreeWoodShape.ConnectionMask(ids[dir], nbStates[dir], backIds, backStates);
                Assert.Equal((mask >> dir) & 1, (neighbourMask >> back) & 1);
            }
        }
    }

    private static uint Next(ref uint seed)
    {
        seed = seed * 1664525u + 1013904223u;
        return seed >> 8;
    }

    [Fact]
    public void DetachedNeverConnects()
    {
        var oak = Oak;
        foreach (var t in Thicknesses)
        {
            var detached = WoodState.Encode(t, WoodState.ParentDetached, false);
            for (var dir = 0; dir < 6; dir++)
            foreach (var other in SweepStates())
            {
                Assert.False(TreeWoodShape.Connects(oak, detached, oak, other, dir));
                Assert.False(TreeWoodShape.Connects(oak, other, oak, detached, dir));
            }
        }

        // A parent pointer at a different block never connects: another species, or a block
        // that is not tree wood at all
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var dir = 0; dir < 6; dir++)
        {
            var pointsAtB = WoodState.Encode(5, dir, false);
            var pointsAtA = WoodState.Encode(5, TreeWoodShape.Opposite(dir), false);
            Assert.True(TreeWoodShape.Connects(oak, pointsAtB, oak, pointsAtA, dir));
            Assert.False(TreeWoodShape.Connects(oak, pointsAtB, Spruce, pointsAtA, dir));
            Assert.False(TreeWoodShape.Connects(Spruce, pointsAtB, oak, pointsAtA, dir));
            Assert.False(TreeWoodShape.Connects(oak, pointsAtB, stone, pointsAtA, dir));
        }
    }

    // The cell under test, in the middle of a chunk far above the terrain
    private const int Cx = 8, Cy = 8, Cz = 8;

    private static List<Vector3> VerticesInCell(ChunkMeshData mesh) =>
        mesh.Vertices.Concat(mesh.TranslucentVertices)
            .Select(v => v.Position)
            .Where(p => p.X >= Cx - Eps && p.X <= Cx + 1 + Eps
                        && p.Y >= Cy - Eps && p.Y <= Cy + 1 + Eps
                        && p.Z >= Cz - Eps && p.Z <= Cz + 1 + Eps)
            .ToList();

    private static bool Near(Vector3 a, Vector3 b) =>
        MathF.Abs(a.X - b.X) <= Eps && MathF.Abs(a.Y - b.Y) <= Eps && MathF.Abs(a.Z - b.Z) <= Eps;

    [Theory]
    // Bit i = connected toward CubeFaces[i]; the lowest set bit is the parent
    [InlineData(0, 0b100000)]          // a twig on its parent below, nothing else
    [InlineData(3, 0b100011)]          // parent -Z, children +Z and -Y
    [InlineData(7, 0b111111)]          // connected on every face
    [InlineData(12, 0b011100)]         // parent -X, children +X and +Y
    [InlineData(14, 0b001001)]         // parent -Z, child +X: a bend
    public void HitboxesCoverExactlyTheMesh(int thickness, int connected)
    {
        var oak = Oak;
        var (manager, chunk) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);

        // The parent is the lowest connected direction; every other connected neighbour is a
        // child whose own parent points back here. One unconnected oak neighbour (parent
        // pointing away) sits in the first free direction, to show touching is not joining.
        var parent = -1;
        for (var dir = 0; dir < 6; dir++)
            if ((connected >> dir & 1) != 0) { parent = dir; break; }

        var state = WoodState.Encode(thickness, parent, false);
        chunk.SetBlockRaw(Cx, Cy, Cz, oak, state);
        Span<byte> ids = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        var strangerPlaced = false;
        for (var dir = 0; dir < 6; dir++)
        {
            var o = Offset(dir);
            var neighbourThickness = (thickness + 4 + dir * 3) % WoodState.FullCube; // always thin
            byte nbState;
            if ((connected >> dir & 1) != 0)
                nbState = dir == parent
                    ? WoodState.Encode(neighbourThickness, WoodState.ParentRoot, false)
                    : WoodState.Encode(neighbourThickness, TreeWoodShape.Opposite(dir), false);
            else if (!strangerPlaced)
            {
                strangerPlaced = true;
                nbState = WoodState.Encode(neighbourThickness, dir, false); // points away from us
            }
            else continue;

            chunk.SetBlockRaw(Cx + (int)o.X, Cy + (int)o.Y, Cz + (int)o.Z, oak, nbState);
            ids[dir] = oak;
            nbStates[dir] = nbState;
        }

        var boxes = TreeWoodShape.GetBoxes(oak, state, ids, nbStates);
        Assert.Equal(1 + System.Numerics.BitOperations.PopCount((uint)connected), boxes.Length);

        var mesh = chunk.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        var vertices = VerticesInCell(mesh);
        Assert.NotEmpty(vertices);

        var cell = new Vector3(Cx, Cy, Cz);
        var boxMin = new Vector3(float.MaxValue);
        var boxMax = new Vector3(float.MinValue);
        foreach (var box in boxes)
        {
            boxMin = Vector3.Min(boxMin, cell + box.Min);
            boxMax = Vector3.Max(boxMax, cell + box.Max);
        }

        var meshMin = vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var meshMax = vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Assert.True(Near(meshMin, boxMin), $"mesh min {meshMin} vs hitbox min {boxMin}");
        Assert.True(Near(meshMax, boxMax), $"mesh max {meshMax} vs hitbox max {boxMax}");

        foreach (var box in boxes)
        for (var corner = 0; corner < 8; corner++)
        {
            var p = cell + new Vector3(
                (corner & 1) == 0 ? box.Min.X : box.Max.X,
                (corner & 2) == 0 ? box.Min.Y : box.Max.Y,
                (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
            Assert.True(vertices.Any(v => Near(v, p)), $"hitbox corner {p} is not a mesh vertex");
        }

        // Every vertex lies on the surface of some box: nothing is drawn outside the hitboxes
        foreach (var v in vertices)
            Assert.True(boxes.Any(b => Inside(v - cell, b)), $"mesh vertex {v} lies outside every hitbox");
    }

    private static bool Inside(Vector3 p, BlockHitbox box) =>
        p.X >= box.Min.X - Eps && p.X <= box.Max.X + Eps
        && p.Y >= box.Min.Y - Eps && p.Y <= box.Max.Y + Eps
        && p.Z >= box.Min.Z - Eps && p.Z <= box.Max.Z + Eps;

    [Fact]
    public void AFullCubeIsOneBoxDrawnOnTheCellBoundary()
    {
        var oak = Oak;
        var (manager, chunk) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var state = WoodState.Encode(WoodState.FullCube, 5, false);
        chunk.SetBlockRaw(Cx, Cy, Cz, oak, state);
        // A thin child above, joined to the full cube through its parent pointer
        var child = WoodState.Encode(4, 5, false);
        chunk.SetBlockRaw(Cx, Cy + 1, Cz, oak, child);

        Span<byte> ids = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        ids[4] = oak;
        nbStates[4] = child;
        var boxes = TreeWoodShape.GetBoxes(oak, state, ids, nbStates);
        Assert.Equal(BlockHitbox.FullCube, Assert.Single(boxes));

        var mesh = chunk.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        var vertices = VerticesInCell(mesh);
        Assert.NotEmpty(vertices);
        foreach (var v in vertices)
        {
            var onBoundary = MathF.Abs(v.X - Cx) <= Eps || MathF.Abs(v.X - Cx - 1) <= Eps
                             || MathF.Abs(v.Y - Cy) <= Eps || MathF.Abs(v.Y - Cy - 1) <= Eps
                             || MathF.Abs(v.Z - Cz) <= Eps || MathF.Abs(v.Z - Cz - 1) <= Eps;
            Assert.True(onBoundary, $"full-cube vertex {v} lies inside the cell");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StackedFullCubesHideTheirSharedFaces(bool detached)
    {
        var oak = Oak;
        var (manager, chunk) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var lower = detached ? WoodState.Detached : WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false);
        var upper = detached ? WoodState.Detached : WoodState.Encode(WoodState.FullCube, 5, false);
        chunk.SetBlockRaw(Cx, Cy, Cz, oak, lower);
        chunk.SetBlockRaw(Cx, Cy + 1, Cz, oak, upper);

        var mesh = chunk.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);

        var shared = mesh.Vertices.Where(v =>
            (v.Normal & 7) is 4 or 5 && MathF.Abs(v.Position.Y - (Cy + 1)) <= Eps).ToList();
        Assert.Empty(shared);
        // The outer top and bottom still draw
        Assert.Contains(mesh.Vertices, v => (v.Normal & 7) == 4 && MathF.Abs(v.Position.Y - (Cy + 2)) <= Eps);
        Assert.Contains(mesh.Vertices, v => (v.Normal & 7) == 5 && MathF.Abs(v.Position.Y - Cy) <= Eps);
    }
}
