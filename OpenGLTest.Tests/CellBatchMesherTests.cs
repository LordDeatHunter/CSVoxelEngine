using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

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

            // Every cell some vertex lies on or inside, the vertex grown by a small epsilon.
            var touched = new HashSet<(int, int, int)>();
            foreach (var v in AllVertices(CellBatchMesher.Build(cells)))
            {
                var lo = v.Position - new Vector3(Eps);
                var hi = v.Position + new Vector3(Eps);
                for (var x = (int)MathF.Floor(lo.X); x <= (int)MathF.Floor(hi.X); x++)
                for (var y = (int)MathF.Floor(lo.Y); y <= (int)MathF.Floor(hi.Y); y++)
                for (var z = (int)MathF.Floor(lo.Z); z <= (int)MathF.Floor(hi.Z); z++)
                    touched.Add((x, y, z));
            }

            foreach (var c in cells)
            {
                var exposed = FaceOffsets.Any(o => !occupied.Contains((c.X + o.X, c.Y + o.Y, c.Z + o.Z)));
                if (exposed)
                    Assert.True(touched.Contains((c.X, c.Y, c.Z)),
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
