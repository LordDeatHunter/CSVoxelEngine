using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A felled tree is meshed by the real chunk mesher inside a scratch chunk that stands far from
/// the tree, so its biome tint must come from the tree's own footprint
/// (<see cref="BiomeTint.FieldsAt"/> at the fell set's minimum cell), never from the scratch
/// chunk's position, and its wood stays untinted.
/// </summary>
public sealed class TopplingTreeTintTests
{
    public TopplingTreeTintTests() => TestContent.EnsureLoaded();

    /// <summary>The chunk position <c>TopplingTree.BuildMeshData</c> meshes its scratch chunk at.</summary>
    private const int ScratchChunk = 256;

    private static BlockType Leaves => ContentRegistry.RequireBlock("leaves");

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    private static (byte R, byte G, byte B) ShadeOf(int face, int ao, Vector3 tint)
    {
        BiomeTint.Shade(Chunk.ShadeLut[(face << 2) | ao], tint, out var r, out var g, out var b);
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

    /// <summary>A world cell whose foliage field differs clearly from the scratch chunk's own.</summary>
    private static bool TryFindDiscriminatingOrigin(out Vector3 min)
    {
        var scratchField = BiomeTint.FieldsFor(ScratchChunk, ScratchChunk).Foliage;
        for (var cx = -200; cx <= 200; cx += 8)
        for (var cz = -200; cz <= 200; cz += 8)
        {
            // Off the 16-block lattice, so the origin-relative field is exercised too.
            var wx = cx * Chunk.Size + 5;
            var wz = cz * Chunk.Size + 5;
            if (MaxCornerDiff(BiomeTint.FieldsAt(wx, wz).Foliage, scratchField) <= 8f / 255f)
                continue;
            min = new Vector3(wx, 40, wz);
            return true;
        }

        min = default;
        return false;
    }

    private static IEnumerable<Vertex> AllVertices(ChunkMeshData mesh) =>
        mesh.Vertices.Concat(mesh.TranslucentVertices);

    [Fact]
    public void FelledLeavesWearTheFieldOfTheTreesFootprint()
    {
        Assert.True(TryFindDiscriminatingOrigin(out var min),
            "no origin within +-200 chunks has a foliage field distinct from the scratch chunk's");

        var leaves = Leaves;
        var bt = leaves.BiomeTint ?? throw new InvalidOperationException("leaves declare no biomeTint");
        List<TopplingTree.FellBlock> blocks =
        [
            new(min, leaves.Id),
            new(min + Vector3.UnitY, leaves.Id),
            new(min + Vector3.UnitX, leaves.Id),
        ];

        // Pivot at min: a vertex position is then its scratch-local position, i.e. the offset
        // into the footprint the field is sampled over.
        var mesh = TopplingTree.BuildMeshData(blocks, pivot: min);

        var footprint = BiomeTint.FieldsAt((int)min.X, (int)min.Z).Foliage;
        var scratchField = BiomeTint.FieldsFor(ScratchChunk, ScratchChunk).Foliage;
        var checkedCount = 0;
        var discriminated = 0;
        foreach (var v in AllVertices(mesh))
        {
            var face = v.Normal & 7;
            if (v.Layer != bt.Tinted[face])
                continue;

            checkedCount++;
            Assert.True(v.IsBiomeTinted, $"felled leaf vertex at {v.Position} lacks the biome-tint flag");

            var expected = footprint.Sample(v.Position.X, v.Position.Z);
            var wrong = scratchField.Sample(v.Position.X, v.Position.Z);
            var matched = false;
            for (var ao = 0; ao < 4; ao++)
            {
                if (!Near(Rgb(v), ShadeOf(face, ao, expected), 1))
                    continue;
                matched = true;

                // At this very shade, the scratch chunk's field would have baked visibly other
                // bytes: the vertex must not carry them.
                var wrongShade = ShadeOf(face, ao, wrong);
                if (Near(ShadeOf(face, ao, expected), wrongShade, 2))
                    continue;
                discriminated++;
                Assert.False(Near(Rgb(v), wrongShade, 1),
                    $"felled leaf vertex at {v.Position} wears the scratch chunk's tint");
            }

            Assert.True(matched,
                $"felled leaf vertex at {v.Position} rgb {Rgb(v)} is not shade * the footprint's foliage tint");
        }

        Assert.True(checkedCount > 0, "the felled leaves emitted no tinted vertex");
        Assert.True(discriminated > 0, "no vertex could tell the footprint's field from the scratch chunk's");
    }

    [Fact]
    public void FelledWoodStaysUntinted()
    {
        var log = ContentRegistry.RequireBlock("log");
        Assert.Null(log.BiomeTint);
        Assert.True(TryFindDiscriminatingOrigin(out var min));

        List<TopplingTree.FellBlock> blocks =
        [
            new(min, log.Id),
            new(min + Vector3.UnitY, log.Id),
            new(min + Vector3.UnitY * 2, log.Id),
        ];
        var mesh = TopplingTree.BuildMeshData(blocks, pivot: min);

        var vertices = AllVertices(mesh).ToList();
        Assert.NotEmpty(vertices);
        foreach (var v in vertices)
        {
            Assert.False(v.IsBiomeTinted, $"felled wood vertex at {v.Position} carries the biome-tint flag");
            var (r, g, b) = Rgb(v);
            Assert.True(r == g && g == b, $"felled wood vertex at {v.Position} is not grey: {(r, g, b)}");
        }
    }
}
