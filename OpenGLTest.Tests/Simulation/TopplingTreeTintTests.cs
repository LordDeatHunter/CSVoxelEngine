using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// A felled tree is meshed by the real chunk mesher with each chunk at the cells' real position,
/// so its biome tint must come from the field of the chunk column each cell stood in
/// (<see cref="BiomeTint.FieldsFor"/>), never from some other place, and its wood stays untinted.
/// </summary>
public sealed class TopplingTreeTintTests
{
    public TopplingTreeTintTests() => TestContent.EnsureLoaded();

    /// <summary>A chunk column far from the test trees, whose climate a wrongly placed mesh would wear.</summary>
    private const int FarChunk = 256;

    private static BlockType Leaves => ContentRegistry.RequireBlock("oak_leaves");

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    private static (byte R, byte G, byte B) ShadeOf(int face, int ao, Vector3 tint)
    {
        BiomeTint.Shade(ChunkFaceTables.ShadeLut[(face << 2) | ao], tint, out var r, out var g, out var b);
        return (r, g, b);
    }

    private static bool Near((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static float MaxChannelDiff(Vector3 a, Vector3 b)
    {
        var d = Vector3.Abs(a - b);
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
    }

    private static float MaxCornerDiff(BiomeTintField a, BiomeTintField b) =>
        new[]
        {
            MaxChannelDiff(a.C00, b.C00), MaxChannelDiff(a.C10, b.C10),
            MaxChannelDiff(a.C01, b.C01), MaxChannelDiff(a.C11, b.C11),
        }.Max();

    /// <summary>How far the test trees' minimum corner sits from the pivot column, on X and Z.</summary>
    private const int CornerOffset = 5;

    /// <summary>
    /// A pivot cell (a few blocks past a chunk border, so the tree's minimum corner lies in the
    /// chunk column before it) whose column's foliage field differs clearly from the far
    /// column's.
    /// </summary>
    private static bool TryFindDiscriminatingPivot(out Vector3 pivotCell)
    {
        var farField = BiomeTint.FieldsFor(FarChunk, FarChunk).Foliage;
        for (var cx = -200; cx <= 200; cx += 8)
        for (var cz = -200; cz <= 200; cz += 8)
        {
            var wx = cx * Chunk.Size + CornerOffset - 2;
            var wz = cz * Chunk.Size + CornerOffset - 2;
            if (MaxCornerDiff(BiomeTint.FieldsFor(wx >> Chunk.SizeShift, wz >> Chunk.SizeShift).Foliage, farField) <= 8f / 255f)
                continue;
            pivotCell = new Vector3(wx, 40, wz);
            return true;
        }

        pivotCell = default;
        return false;
    }

    private static IEnumerable<Vertex> AllVertices(IEnumerable<ChunkMeshData> meshes) =>
        meshes.SelectMany(mesh => mesh.Vertices.Concat(mesh.TranslucentVertices));

    [Fact]
    public void FelledLeavesWearTheFieldOfTheChunkColumnTheyStoodIn()
    {
        Assert.True(TryFindDiscriminatingPivot(out var pivotCell),
            "no pivot within +-200 chunks has a foliage field distinct from the far column's");

        var leaves = Leaves;
        var bt = leaves.BiomeTint ?? throw new InvalidOperationException("leaves declare no biomeTint");
        // The minimum corner sits across a chunk border from the pivot column, so the tree spans
        // two columns and each cell must wear its own column's field
        var corner = pivotCell - new Vector3(CornerOffset, 0, CornerOffset);
        List<TopplingTree.FellBlock> blocks =
        [
            new(pivotCell, leaves.Id, LeafState.Encode(1, false)),
            new(pivotCell + Vector3.UnitY, leaves.Id, LeafState.Encode(1, false)),
            new(pivotCell + Vector3.UnitX, leaves.Id, LeafState.Encode(1, false)),
            new(corner, leaves.Id, LeafState.Encode(1, false)),
        ];

        var pivot = new Vector3(pivotCell.X + 0.5f, pivotCell.Y, pivotCell.Z + 0.5f);
        var meshes = TopplingTree.BuildMeshData(blocks, pivot);

        var farField = BiomeTint.FieldsFor(FarChunk, FarChunk).Foliage;
        var checkedCount = 0;
        var discriminated = 0;
        foreach (var v in AllVertices(meshes))
        {
            var face = v.Normal & 7;
            if (v.Layer != bt.Tinted[face])
                continue;

            checkedCount++;
            Assert.True(v.IsBiomeTinted, $"felled leaf vertex at {v.Position} lacks the biome-tint flag");

            // A vertex position is pivot-relative; the field it must wear is its world column's,
            // sampled at its offset into that column
            var world = v.Position + pivot;
            int cx = (int)MathF.Floor(world.X) >> Chunk.SizeShift, cz = (int)MathF.Floor(world.Z) >> Chunk.SizeShift;
            var local = world - new Vector3(cx * Chunk.Size, 0f, cz * Chunk.Size);
            var expected = BiomeTint.FieldsFor(cx, cz).Foliage.Sample(local.X, local.Z);
            var wrong = farField.Sample(local.X, local.Z);
            var matched = false;
            for (var ao = 0; ao < 4; ao++)
            {
                if (!Near(Rgb(v), ShadeOf(face, ao, expected), 1))
                    continue;
                matched = true;

                // At this very shade, the far column's field would have baked visibly other
                // bytes: the vertex must not carry them.
                var wrongShade = ShadeOf(face, ao, wrong);
                if (Near(ShadeOf(face, ao, expected), wrongShade, 2))
                    continue;
                discriminated++;
                Assert.False(Near(Rgb(v), wrongShade, 1),
                    $"felled leaf vertex at {v.Position} wears the far column's tint");
            }

            Assert.True(matched,
                $"felled leaf vertex at {v.Position} rgb {Rgb(v)} is not shade * its own column's foliage tint");
        }

        Assert.True(checkedCount > 0, "the felled leaves emitted no tinted vertex");
        Assert.True(discriminated > 0, "no vertex could tell its own column's field from the far one's");
    }

    [Fact]
    public void FelledWoodStaysUntinted()
    {
        var log = ContentRegistry.RequireBlock("oak_log");
        Assert.Null(log.BiomeTint);
        Assert.True(TryFindDiscriminatingPivot(out var pivotCell));

        List<TopplingTree.FellBlock> blocks =
        [
            new(pivotCell, log.Id, WoodState.Detached),
            new(pivotCell + Vector3.UnitY, log.Id, WoodState.Detached),
            new(pivotCell + Vector3.UnitY * 2, log.Id, WoodState.Detached),
        ];
        var meshes = TopplingTree.BuildMeshData(blocks, new Vector3(pivotCell.X + 0.5f, pivotCell.Y, pivotCell.Z + 0.5f));

        var vertices = AllVertices(meshes).ToList();
        Assert.NotEmpty(vertices);
        foreach (var v in vertices)
        {
            Assert.False(v.IsBiomeTinted, $"felled wood vertex at {v.Position} carries the biome-tint flag");
            var (r, g, b) = Rgb(v);
            Assert.True(r == g && g == b, $"felled wood vertex at {v.Position} is not gray: {(r, g, b)}");
        }
    }
}
