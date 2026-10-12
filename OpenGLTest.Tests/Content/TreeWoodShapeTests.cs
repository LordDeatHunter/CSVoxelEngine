using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Content;

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

        // A cell's mask bit toward a neighbor equals that neighbor's mask bit back toward it
        Span<byte> ids = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        Span<byte> backIds = stackalloc byte[6];
        Span<byte> backStates = stackalloc byte[6];
        var seed = 12345u;
        for (var trial = 0; trial < 500; trial++)
        {
            var center = states[(int)(Next(ref seed) % (uint)states.Length)];
            for (var dir = 0; dir < 6; dir++)
            {
                // Mostly oak, sometimes air or spruce, so foreign neighbors are in the sweep too
                var pick = Next(ref seed) % 8;
                ids[dir] = pick == 0 ? (byte)0 : pick == 1 ? Spruce : oak;
                nbStates[dir] = states[(int)(Next(ref seed) % (uint)states.Length)];
            }

            var mask = TreeWoodShape.ConnectionMask(oak, center, ids, nbStates);
            for (var dir = 0; dir < 6; dir++)
            {
                backIds.Clear();
                backStates.Clear();
                var back = TreeWoodShape.Opposite(dir);
                backIds[back] = oak;
                backStates[back] = center;
                var neighborMask = TreeWoodShape.ConnectionMask(ids[dir], nbStates[dir], backIds, backStates);
                Assert.Equal((mask >> dir) & 1, (neighborMask >> back) & 1);
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

        // The parent is the lowest connected direction; every other connected neighbor is a
        // child whose own parent points back here. One unconnected oak neighbor (parent
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
            var neighborThickness = (thickness + 4 + dir * 3) % WoodState.FullCube; // always thin
            byte nbState;
            if ((connected >> dir & 1) != 0)
                nbState = dir == parent
                    ? WoodState.Encode(neighborThickness, WoodState.ParentRoot, false)
                    : WoodState.Encode(neighborThickness, TreeWoodShape.Opposite(dir), false);
            else if (!strangerPlaced)
            {
                strangerPlaced = true;
                nbState = WoodState.Encode(neighborThickness, dir, false); // points away from us
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

    [Fact]
    public void AThinCellPlugsIntoAnOccluderButFloatsOverAir()
    {
        var oak = Oak;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var state = WoodState.Encode(5, WoodState.ParentRoot, false);
        var half = TreeWoodShape.HalfWidth(WoodState.Thickness(state));

        Span<byte> ids = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        var floating = TreeWoodShape.GetBoxes(oak, state, ids, nbStates);
        Assert.Equal(0.5f - half, floating.Min(b => b.Min.Y), Eps);

        ids[5] = stone;
        var grounded = TreeWoodShape.GetBoxes(oak, state, ids, nbStates);
        Assert.Equal(0f, grounded.Min(b => b.Min.Y), Eps);
    }

    [Fact]
    public void AThinCellRootedOnSolidGroundDrawsDownToTheGroundInBothMeshAndHitboxes()
    {
        var oak = Oak;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var state = WoodState.Encode(5, WoodState.ParentRoot, false);
        var (manager, chunk) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        // The wood sits in the center chunk's bottom row and the ground in the chunk below it, so
        // the center chunk's mesh holds only the wood and the check is not confused by the ground.
        chunk.SetBlockRaw(Cx, 0, Cz, oak, state);
        manager.Chunks[MeshTestWorld.HighAbove + new Vector3(0, -1, 0)]
            .SetBlockRaw(Cx, Chunk.Size - 1, Cz, stone, 0);

        Span<byte> ids = stackalloc byte[6];
        Span<byte> nbStates = stackalloc byte[6];
        ids[5] = stone;
        var boxes = TreeWoodShape.GetBoxes(oak, state, ids, nbStates);

        var mesh = chunk.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        var vertices = mesh.Vertices.Concat(mesh.TranslucentVertices).Select(v => v.Position).ToList();
        Assert.NotEmpty(vertices);

        var origin = new Vector3(Cx, 0, Cz);
        var boxMin = boxes.Aggregate(new Vector3(float.MaxValue), (m, b) => Vector3.Min(m, origin + b.Min));
        var boxMax = boxes.Aggregate(new Vector3(float.MinValue), (m, b) => Vector3.Max(m, origin + b.Max));
        var meshMin = vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var meshMax = vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Assert.True(Near(meshMin, boxMin), $"mesh min {meshMin} vs hitbox min {boxMin}");
        Assert.True(Near(meshMax, boxMax), $"mesh max {meshMax} vs hitbox max {boxMax}");
        Assert.Equal(origin.Y, boxMin.Y, Eps);
    }

    // One placed tree-wood cell of a rim test configuration, chunk-local
    private readonly record struct RimCell(int X, int Y, int Z, byte State);

    private static byte ShellState(int dx, int dz) =>
        WoodState.Encode(WoodState.FullCube, TreeRasterizer.ShellParentDir(dx, dz), true);

    private static byte CoreState => WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false);

    // The rim configurations, all one wood, inside the center chunk and away from its borders
    private static IEnumerable<(string Name, RimCell[] Cells)> RimConfigurations()
    {
        yield return ("lone shell", [new RimCell(Cx, Cy, Cz, ShellState(1, 0))]);

        var slice = new List<RimCell>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            slice.Add(new RimCell(Cx + dx, Cy, Cz + dz, dx == 0 && dz == 0 ? CoreState : ShellState(dx, dz)));
        yield return ("3x3 slice", slice.ToArray());

        // Lone shells over the core and over a corner shell: diagonal to each other, so both
        // stay fully beveled, each narrower than the cell below it
        var stack = new List<RimCell>(slice)
        {
            new(Cx, Cy + 1, Cz, ShellState(1, 0)),
            new(Cx + 1, Cy + 1, Cz + 1, ShellState(1, 1)),
        };
        yield return ("two-slice stack", stack.ToArray());

        yield return ("shell beside a detached log",
        [
            new RimCell(Cx, Cy, Cz, ShellState(1, 0)),
            new RimCell(Cx + 1, Cy, Cz, WoodState.Detached),
        ]);
    }

    private static byte PixelRimWood => TreeSpeciesRegistry.All.First(s => s.Rim == TrunkRim.Pixel).WoodId;

    // The boxes of one placed cell, from its neighbors in the configuration
    private static BlockHitbox[] BoxesOf(byte wood, Dictionary<(int, int, int), byte> cells, RimCell c)
    {
        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        for (var dir = 0; dir < 6; dir++)
        {
            var o = Offset(dir);
            if (!cells.TryGetValue((c.X + (int)o.X, c.Y + (int)o.Y, c.Z + (int)o.Z), out var s)) continue;
            ids[dir] = wood;
            states[dir] = s;
        }

        return TreeWoodShape.GetBoxes(wood, c.State, ids, states);
    }

    // 16x16 pixel-center coverage of the XZ rectangle [min, max] (cell-local)
    private static void Rasterize(Vector3 min, Vector3 max, int[,] px)
    {
        for (var i = 0; i < 16; i++)
        for (var k = 0; k < 16; k++)
        {
            var x = (i + 0.5f) / 16f;
            var z = (k + 0.5f) / 16f;
            if (x > min.X && x < max.X && z > min.Z && z < max.Z) px[i, k]++;
        }
    }

    private static int[,] Footprint(IEnumerable<BlockHitbox> boxes)
    {
        var px = new int[16, 16];
        foreach (var b in boxes) Rasterize(b.Min, b.Max, px);
        return px;
    }

    private static bool IsWhole(int[,] px)
    {
        foreach (var p in px)
            if (p == 0) return false;
        return true;
    }

    // The cell holding a quad's material: just behind its center, against its normal
    private static (int, int, int) OwnerOf(Vertex[] q)
    {
        var center = (q[0].Position + q[1].Position + q[2].Position + q[3].Position) / 4f;
        var p = center - Chunk.CubeFaces[q[0].Normal & 7].normal * 0.01f;
        return ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
    }

    private static Vector3 QuadMin(Vertex[] q) => q.Select(v => v.Position).Aggregate(Vector3.Min);

    private static Vector3 QuadMax(Vertex[] q) => q.Select(v => v.Position).Aggregate(Vector3.Max);

    [Fact]
    public void RimMeshCoversExactlyTheRimHitboxes()
    {
        var wood = PixelRimWood;
        var beveled = 0;
        foreach (var (name, cells) in RimConfigurations())
        {
            var (manager, chunk) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
            var byPos = new Dictionary<(int, int, int), byte>();
            foreach (var c in cells)
            {
                chunk.SetBlockRaw(c.X, c.Y, c.Z, wood, c.State);
                byPos[(c.X, c.Y, c.Z)] = c.State;
            }

            var mesh = chunk.GenerateMeshData(manager, greedy: false);
            Assert.NotNull(mesh);
            Assert.Equal(0, mesh.Vertices.Count % 4);
            var quads = new Dictionary<(int, int, int), List<Vertex[]>>();
            for (var i = 0; i < mesh.Vertices.Count; i += 4)
            {
                var q = mesh.Vertices.GetRange(i, 4).ToArray();
                var owner = OwnerOf(q);
                if (!quads.TryGetValue(owner, out var list)) quads[owner] = list = [];
                list.Add(q);
            }

            foreach (var c in cells)
            {
                var origin = new Vector3(c.X, c.Y, c.Z);
                var boxes = BoxesOf(wood, byPos, c);
                var mine = quads.GetValueOrDefault((c.X, c.Y, c.Z)) ?? [];
                Assert.NotEmpty(mine);
                if (boxes.Length > 1) beveled++;
                var self = Footprint(boxes);

                // Top and bottom: a beveled shell draws its footprint minus what the same-wood
                // cell beyond covers; a plain cube draws its whole cap unless the cell beyond
                // fills it, so a cube under a beveled shell keeps its full top
                foreach (var (face, dy) in new[] { (4, 1), (5, -1) })
                {
                    var beyond = byPos.TryGetValue((c.X, c.Y + dy, c.Z), out var bs)
                        ? Footprint(BoxesOf(wood, byPos, new RimCell(c.X, c.Y + dy, c.Z, bs)))
                        : new int[16, 16];
                    var planeY = c.Y + (dy > 0 ? 1 : 0);
                    var drawn = new int[16, 16];
                    foreach (var q in mine.Where(q => (q[0].Normal & 7) == face))
                    {
                        Assert.All(q, v => Assert.True(MathF.Abs(v.Position.Y - planeY) <= Eps,
                            $"{name}: cap vertex {v.Position} off the cell's face plane"));
                        Rasterize(QuadMin(q) - origin, QuadMax(q) - origin, drawn);
                    }

                    var beyondWhole = IsWhole(beyond);
                    for (var i = 0; i < 16; i++)
                    for (var k = 0; k < 16; k++)
                    {
                        var expected = boxes.Length > 1 ? self[i, k] > 0 && beyond[i, k] == 0 : !beyondWhole;
                        Assert.True(drawn[i, k] == (expected ? 1 : 0),
                            $"{name}: cell {c} face {face} pixel ({i},{k}) drawn {drawn[i, k]}x, expected {expected}");
                    }
                }

                // Every vertex lies on the surface of one of the cell's boxes
                foreach (var q in mine)
                foreach (var v in q)
                    Assert.True(boxes.Any(b => OnSurface(v.Position - origin, b)),
                        $"{name}: vertex {v.Position} of cell {c} lies on no hitbox surface");

                // Every side quad lies in one box's wall plane, within that box's extent
                foreach (var q in mine.Where(q => (q[0].Normal & 7) < 4))
                {
                    var face = q[0].Normal & 7;
                    var min = QuadMin(q) - origin;
                    var max = QuadMax(q) - origin;
                    Assert.True(boxes.Any(b => InWallOf(min, max, b, face)),
                        $"{name}: side quad {min}..{max} (face {face}) of cell {c} lies in no box wall");
                }
            }

            if (name == "shell beside a detached log")
            {
                // The detached log draws its whole face toward the shell's carved side
                var facing = (quads.GetValueOrDefault((Cx + 1, Cy, Cz)) ?? [])
                    .Where(q => (q[0].Normal & 7) == 2).ToList();
                Assert.NotEmpty(facing);
                var area = facing.Sum(q =>
                {
                    var size = QuadMax(q) - QuadMin(q);
                    return size.Y * size.Z;
                });
                Assert.True(MathF.Abs(area - 1f) <= Eps, $"the log's face toward the shell covers {area}");
            }
        }

        Assert.True(beveled > 0, "no configuration produced a beveled shell");
    }

    private static bool OnSurface(Vector3 p, BlockHitbox b) =>
        Inside(p, b)
        && (MathF.Abs(p.X - b.Min.X) <= Eps || MathF.Abs(p.X - b.Max.X) <= Eps
            || MathF.Abs(p.Y - b.Min.Y) <= Eps || MathF.Abs(p.Y - b.Max.Y) <= Eps
            || MathF.Abs(p.Z - b.Min.Z) <= Eps || MathF.Abs(p.Z - b.Max.Z) <= Eps);

    // Whether the axis-aligned side quad [min, max] lies in box b's wall toward face
    private static bool InWallOf(Vector3 min, Vector3 max, BlockHitbox b, int face)
    {
        var plane = face switch { 0 => b.Min.Z, 1 => b.Max.Z, 2 => b.Min.X, _ => b.Max.X };
        var at = face < 2 ? min.Z : min.X;
        return MathF.Abs(at - plane) <= Eps && Inside(min, b) && Inside(max, b);
    }
}
