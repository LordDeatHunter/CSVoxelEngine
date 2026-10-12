using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// <see cref="CellBatchMesher"/> meshes any cell set with the real chunk mesher across tiled
/// scratch chunks standing at their real positions: the output is in world cell coordinates, no
/// exposed cell is lost at a chunk seam (the property a single 16^3 window violates), every
/// touched chunk yields its own mesh and the result is deterministic.
/// </summary>
public sealed class CellBatchMesherTests
{
    public CellBatchMesherTests() => TestContent.EnsureLoaded();

    private const float Eps = 1e-3f;

    private static VirtualTreeSpace Flat() => new(static (_, _) => 0);

    // A mature tree rooted on a chunk corner (x = z = 0), so its crown falls in negative and
    // positive chunks alike.
    private static List<TreeCell> GrowAtCorner(TreeSpecies species)
    {
        var space = Flat();
        var steps = TreeGrower.MaturityStepsAt(species, 0, 1, 0, WorldSeed.Default);
        var graph = TreeGrower.GrowSteps(species, 0, 1, 0, space, WorldSeed.Default, steps, applyFeatures: true);
        return TreeRasterizer.Rasterize(graph, species, space);
    }

    private static IEnumerable<Vertex> AllVertices(IEnumerable<ChunkMeshData> meshes) =>
        meshes.SelectMany(m => m.Vertices.Concat(m.TranslucentVertices));

    /// <summary>Every quad a mesh draws, as its four corners.</summary>
    private static IEnumerable<Vector3[]> Quads(IEnumerable<ChunkMeshData> meshes)
    {
        foreach (var mesh in meshes)
        foreach (var (vertices, indices) in new[] { (mesh.Vertices, mesh.Indices), (mesh.TranslucentVertices, mesh.TranslucentIndices) })
            for (var q = 0; q + 5 < indices.Count; q += 6)
                yield return
                [
                    vertices[indices[q]].Position, vertices[indices[q + 1]].Position,
                    vertices[indices[q + 2]].Position, vertices[indices[q + 3]].Position,
                ];
    }

    private static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static bool IsInteger(float f) => MathF.Abs(f - MathF.Round(f)) < Eps;

    /// <summary>
    /// Whether some quad draws this cell: an axis-aligned quad on one of its face planes covering
    /// the face's centre (a greedy merge draws one quad across many cells, so the cells it covers
    /// in the middle hold no vertex of their own), or — for the procedural boxes, tubes and rims —
    /// a vertex landing inside the cell.
    /// </summary>
    private static bool IsDrawn((int X, int Y, int Z) cell, IReadOnlyList<Vector3> vertices, IEnumerable<Vector3[]> quads)
    {
        foreach (var v in vertices)
        {
            if (MathF.Floor(v.X + Eps) == cell.X && MathF.Floor(v.Y + Eps) == cell.Y
                && MathF.Floor(v.Z + Eps) == cell.Z)
                return true;
        }

        var centre = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        foreach (var quad in quads)
        for (var axis = 0; axis < 3; axis++)
        {
            var plane = Axis(quad[0], axis);
            if (!IsInteger(plane) || MathF.Abs(plane - (Axis(centre, axis) + 0.5f)) > 0.5f + Eps) continue;
            var covers = true;
            for (var other = 0; other < 3 && covers; other++)
            {
                if (other == axis) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var corner in quad)
                {
                    lo = MathF.Min(lo, Axis(corner, other));
                    hi = MathF.Max(hi, Axis(corner, other));
                }
                covers = Axis(centre, other) >= lo - Eps && Axis(centre, other) <= hi + Eps;
            }
            if (covers) return true;
        }
        return false;
    }

    private static (int X, int Y, int Z) ChunkOf(int x, int y, int z) =>
        (x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    private static readonly (int X, int Y, int Z)[] FaceOffsets =
        [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    [Fact]
    public void EmptyInputMeshesNothing()
    {
        Assert.Empty(CellBatchMesher.Build(Array.Empty<TreeCell>()));
    }

    [Fact]
    public void EveryVertexLiesInsideTheCellBounds()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var cells = GrowAtCorner(species);
            var min = new Vector3(cells.Min(c => c.X), cells.Min(c => c.Y), cells.Min(c => c.Z));
            var max = new Vector3(cells.Max(c => c.X), cells.Max(c => c.Y), cells.Max(c => c.Z)) + Vector3.One;

            var meshes = CellBatchMesher.Build(cells);
            Assert.NotEmpty(meshes);
            foreach (var v in AllVertices(meshes))
            {
                Assert.InRange(v.Position.X, min.X - Eps, max.X + Eps);
                Assert.InRange(v.Position.Y, min.Y - Eps, max.Y + Eps);
                Assert.InRange(v.Position.Z, min.Z - Eps, max.Z + Eps);
            }
        }
    }

    [Fact]
    public void TheTestTreesStraddleChunkBorders()
    {
        // Precondition of the seam tests below: at least one species' crown crosses the corner.
        var straddles = TreeSpeciesRegistry.All.Any(species =>
            GrowAtCorner(species).Select(c => ChunkOf(c.X, c.Y, c.Z)).Distinct().Count() > 1);
        Assert.True(straddles);
    }

    [Fact]
    public void NoExposedCellIsDropped()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var cells = GrowAtCorner(species);
            var occupied = cells.Select(c => (c.X, c.Y, c.Z)).ToHashSet();

            var meshes = CellBatchMesher.Build(cells);
            var vertices = AllVertices(meshes).Select(v => v.Position).ToList();
            var quads = Quads(meshes).ToList();

            foreach (var c in cells)
            {
                var exposed = FaceOffsets.Any(o => !occupied.Contains((c.X + o.X, c.Y + o.Y, c.Z + o.Z)));
                if (exposed)
                    Assert.True(IsDrawn((c.X, c.Y, c.Z), vertices, quads),
                        $"{species.Name}: exposed cell ({c.X}, {c.Y}, {c.Z}) produced no geometry");
            }
        }
    }

    [Fact]
    public void ACellSetSpanningSeveralChunksMeshesEveryChunkItTouches()
    {
        // Plain full cubes on both sides of the x, y and z chunk borders.
        var block = TerrainGenerator.SurfaceBlock;
        var cells = new List<TreeCell>
        {
            new(-1, 5, -1, block, 0),
            new(0, 5, 0, block, 0),
            new(-1, 5, 3, block, 0),
            new(4, 17, -2, block, 0),
            new(Chunk.Size * 2 + 1, 5, 0, block, 0),
        };
        var expected = cells.Select(c => ChunkOf(c.X, c.Y, c.Z)).ToHashSet();

        var meshes = CellBatchMesher.Build(cells);
        Assert.Equal(expected.Count, meshes.Count);

        var seen = new HashSet<(int, int, int)>();
        foreach (var mesh in meshes)
        {
            Assert.NotEmpty(mesh.Vertices);
            // All of one mesh's geometry is inside a single touched chunk (its centroid's).
            var centroid = mesh.Vertices.Aggregate(Vector3.Zero, (s, v) => s + v.Position) / mesh.Vertices.Count;
            var chunk = ChunkOf((int)MathF.Floor(centroid.X), (int)MathF.Floor(centroid.Y), (int)MathF.Floor(centroid.Z));
            Assert.Contains(chunk, expected);
            Assert.True(seen.Add(chunk));
        }
    }

    [Fact]
    public void MeshingIsDeterministic()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            var cells = GrowAtCorner(species);
            var a = CellBatchMesher.Build(cells);
            var b = CellBatchMesher.Build(cells);
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Vertices, b[i].Vertices);
                Assert.Equal(a[i].Indices, b[i].Indices);
                Assert.Equal(a[i].TranslucentVertices, b[i].TranslucentVertices);
                Assert.Equal(a[i].TranslucentIndices, b[i].TranslucentIndices);
            }
        }
    }
}
