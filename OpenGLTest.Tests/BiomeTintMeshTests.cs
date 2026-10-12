using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The chunk mesher's biome tint: a <c>biomeTint</c> block's tinted quads carry shade * the
/// field's tint and the bit-4 flag, untinted quads stay grey, an overlay is drawn after its
/// base, the tint never splits a greedy merge, and a field override replaces the chunk's own.
/// </summary>
public sealed class BiomeTintMeshTests
{
    public BiomeTintMeshTests() => TestContent.EnsureLoaded();

    private static BlockType Grass => ContentRegistry.RequireBlock("grass");

    private static BiomeTintFaces GrassTint =>
        Grass.BiomeTint ?? throw new InvalidOperationException("grass declares no biomeTint");

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    private static bool Near(Vertex v, byte r, byte g, byte b)
    {
        var (vr, vg, vb) = Rgb(v);
        return Math.Abs(vr - r) <= 1 && Math.Abs(vg - g) <= 1 && Math.Abs(vb - b) <= 1;
    }

    /// <summary>Whether the vertex rgb is shade * <paramref name="tint"/> for some AO level.</summary>
    private static bool MatchesSomeShade(Vertex v, int face, Vector3 tint)
    {
        for (var ao = 0; ao < 4; ao++)
        {
            BiomeTint.Shade(Chunk.ShadeLut[(face << 2) | ao], tint, out var r, out var g, out var b);
            if (Near(v, r, g, b)) return true;
        }
        return false;
    }

    private static int Face(Vertex v) => v.Normal & 7;

    private static IEnumerable<Vertex[]> Quads(List<Vertex> vertices, List<ushort> indices)
    {
        for (var q = 0; q < indices.Count; q += 6)
        {
            var o = indices[q];
            yield return [vertices[o], vertices[o + 1], vertices[o + 2], vertices[o + 3]];
        }
    }

    private static (ChunkManager Manager, Chunk Center) Slab(byte id)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, id, 0);
        return (manager, center);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TintedTopCarriesTheFieldColourAndFlag(bool greedy)
    {
        var (manager, center) = Slab(Grass.Id);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);

        var bt = GrassTint;
        var field = BiomeTint.FieldFor(bt.Palette, (int)center.Position.X, (int)center.Position.Z);
        var tops = mesh.Vertices.Where(v => Face(v) == 4 && v.Layer == bt.Tinted[4]).ToList();
        Assert.NotEmpty(tops);
        Assert.All(tops, v =>
        {
            Assert.True(v.IsBiomeTinted, $"tinted top vertex at {v.Position} is not flagged");
            Assert.True(MatchesSomeShade(v, 4, field.Sample(v.Position.X, v.Position.Z)),
                $"top vertex at {v.Position}: rgb {Rgb(v)} is not shade * the field's tint");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UntintedFacesStayGrey(bool greedy)
    {
        var (stoneManager, stoneCenter) = Slab(ContentRegistry.RequireBlock("stone").Id);
        var stone = stoneCenter.GenerateMeshData(stoneManager, greedy);
        Assert.NotNull(stone);
        Assert.NotEmpty(stone.Vertices);
        Assert.DoesNotContain(stone.Vertices, v => v.IsBiomeTinted);

        var (manager, center) = Slab(Grass.Id);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        var bt = GrassTint;
        // The bottom face and every side's base quad (the dirt under the band) are not tinted
        var untinted = mesh.Vertices.Where(v => Face(v) == 5
                                                || (Face(v) < 4 && v.Layer == bt.Base[Face(v)])).ToList();
        Assert.NotEmpty(untinted);
        Assert.All(untinted, v =>
        {
            Assert.False(v.IsBiomeTinted, $"untinted vertex at {v.Position} face {Face(v)} is flagged");
            var (r, g, b) = Rgb(v);
            Assert.True(r == g && g == b, $"untinted vertex at {v.Position} face {Face(v)} is coloured {Rgb(v)}");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlayDrawsAfterItsBase(bool greedy)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(8, 8, 8, Grass.Id, 0);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);

        var bt = GrassTint;
        var quads = Quads(mesh.Vertices, mesh.Indices).ToList();
        var overlays = 0;
        for (var q = 0; q < quads.Count; q++)
        {
            var face = Face(quads[q][0]);
            if (face >= 4 || !bt.IsOverlay(face) || quads[q][0].Layer != bt.Tinted[face]) continue;
            overlays++;
            var positions = quads[q].Select(v => v.Position).ToHashSet();
            var hasBaseBefore = quads.Take(q).Any(p => Face(p[0]) == face && p[0].Layer == bt.Base[face]
                                                        && p.Select(v => v.Position).ToHashSet().SetEquals(positions));
            Assert.True(hasBaseBefore, $"overlay on face {face} has no coplanar base quad before it");
        }
        Assert.True(overlays > 0, "the lone grass block drew no side overlay");
    }

    [Fact]
    public void TintDoesNotFragmentMerges()
    {
        static int TopQuads(byte id, ushort layer)
        {
            var (manager, center) = Slab(id);
            var mesh = center.GenerateMeshData(manager);
            Assert.NotNull(mesh);
            return Quads(mesh.Vertices, mesh.Indices).Count(q => Face(q[0]) == 4 && q[0].Layer == layer);
        }

        var stone = ContentRegistry.RequireBlock("stone");
        var grassTops = TopQuads(Grass.Id, GrassTint.Tinted[4]);
        var stoneTops = TopQuads(stone.Id, stone.FaceTiles[4]);
        Assert.True(grassTops > 0);
        Assert.True(grassTops <= stoneTops, $"tinted grass top split into {grassTops} quads, stone into {stoneTops}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFieldOverrideReplacesTheChunksOwnField(bool greedy)
    {
        var (manager, center) = Slab(Grass.Id);
        var bt = GrassTint;
        var own = BiomeTint.FieldsFor((int)center.Position.X, (int)center.Position.Z).Field(bt.Palette);

        // Any origin whose field differs visibly from the chunk's own; the palette is not
        // constant, so one exists within a few hundred chunks
        BiomeTintFields? overrideFields = null;
        for (var cx = -200; cx <= 200 && overrideFields == null; cx += 10)
        for (var cz = -200; cz <= 200 && overrideFields == null; cz += 10)
        {
            var candidate = BiomeTint.FieldsAt(cx * Chunk.Size + 5, cz * Chunk.Size + 5);
            var f = candidate.Field(bt.Palette);
            var differs = new[] { f.C00 - own.C00, f.C10 - own.C10, f.C01 - own.C01, f.C11 - own.C11 }
                .Select(Vector3.Abs).Any(d => MathF.Max(d.X, MathF.Max(d.Y, d.Z)) > 8f / 255f);
            if (differs) overrideFields = candidate;
        }
        Assert.True(overrideFields.HasValue, "no origin within +-200 chunks has a different tint field");

        var field = overrideFields.Value.Field(bt.Palette);
        var mesh = center.GenerateMeshData(manager, greedy, overrideFields);
        Assert.NotNull(mesh);

        var lit = Chunk.ShadeLut[(4 << 2) | 3];
        var tops = mesh.Vertices.Where(v => Face(v) == 4 && v.Layer == bt.Tinted[4]).ToList();
        Assert.NotEmpty(tops);
        var discriminating = 0;
        foreach (var v in tops)
        {
            var tint = field.Sample(v.Position.X, v.Position.Z);
            Assert.True(MatchesSomeShade(v, 4, tint), $"vertex at {v.Position} does not wear the override field");

            // The open slab top is unoccluded, so its shade is the lit one: where the two
            // fields give visibly different bytes, the vertex must not wear the chunk's own
            BiomeTint.Shade(lit, tint, out var r, out var g, out var b);
            BiomeTint.Shade(lit, own.Sample(v.Position.X, v.Position.Z), out var or, out var og, out var ob);
            if (Math.Max(Math.Abs(r - or), Math.Max(Math.Abs(g - og), Math.Abs(b - ob))) <= 2) continue;
            discriminating++;
            Assert.False(Near(v, or, og, ob), $"vertex at {v.Position} still wears the chunk's own field");
        }
        Assert.True(discriminating > 0, "no vertex discriminated the override field from the chunk's own");
    }

    private static BlockType Leaves => ContentRegistry.RequireBlock("oak_leaves");

    private static BiomeTintFaces LeavesTint =>
        Leaves.BiomeTint ?? throw new InvalidOperationException("leaves declare no biomeTint");

    /// <summary>The AO levels at which the vertex rgb is shade * <paramref name="tint"/>.</summary>
    private static IEnumerable<int> MatchingAo(Vertex v, int face, Vector3 tint)
    {
        for (var ao = 0; ao < 4; ao++)
        {
            BiomeTint.Shade(Chunk.ShadeLut[(face << 2) | ao], tint, out var r, out var g, out var b);
            if (Near(v, r, g, b)) yield return ao;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TintedLeavesCarryTheFoliageFieldAndKeepSway(bool greedy)
    {
        var (manager, center) = Slab(Leaves.Id);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.Vertices);

        var bt = LeavesTint;
        Assert.Equal(BiomeTintPalette.Foliage, bt.Palette);
        var cx = (int)center.Position.X;
        var cz = (int)center.Position.Z;
        var foliage = BiomeTint.FieldFor(BiomeTintPalette.Foliage, cx, cz);
        var grass = BiomeTint.FieldFor(BiomeTintPalette.Grass, cx, cz);

        var discriminating = 0;
        foreach (var v in mesh.Vertices.Concat(mesh.TranslucentVertices))
        {
            var face = Face(v);
            Assert.True(v.IsBiomeTinted, $"leaf vertex at {v.Position} face {face} is not flagged");
            Assert.True(v.IsSwaying, $"tinted leaf vertex at {v.Position} lost its sway");
            Assert.Equal(bt.Tinted[face], v.Layer);

            var foliageTint = foliage.Sample(v.Position.X, v.Position.Z);
            var aos = MatchingAo(v, face, foliageTint).ToList();
            Assert.True(aos.Count > 0, $"leaf vertex at {v.Position}: rgb {Rgb(v)} is not shade * the foliage field");

            // Where the grass palette would give visibly different bytes at the same shade, the
            // vertex must not be wearing it
            var grassTint = grass.Sample(v.Position.X, v.Position.Z);
            foreach (var ao in aos)
            {
                var shade = Chunk.ShadeLut[(face << 2) | ao];
                BiomeTint.Shade(shade, foliageTint, out var fr, out var fg, out var fb);
                BiomeTint.Shade(shade, grassTint, out var gr, out var gg, out var gb);
                if (Math.Max(Math.Abs(fr - gr), Math.Max(Math.Abs(fg - gg), Math.Abs(fb - gb))) <= 2) continue;
                discriminating++;
                Assert.False(Near(v, gr, gg, gb), $"leaf vertex at {v.Position} wears the grass palette");
            }
        }

        Assert.True(discriminating > 0, "no leaf vertex told the foliage palette apart from the grass one");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TintedLeavesWearOnlyTintedTiles(bool greedy)
    {
        var (manager, center) = Slab(Leaves.Id);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);

        var bt = LeavesTint;
        var tinted = Enumerable.Range(0, 6).Where(bt.IsTinted).Select(f => bt.Tinted[f]).ToHashSet();
        var quads = Quads(mesh.Vertices, mesh.Indices)
            .Concat(Quads(mesh.TranslucentVertices, mesh.TranslucentIndices)).ToList();
        Assert.NotEmpty(quads);
        Assert.All(quads, q =>
        {
            Assert.Contains(q[0].Layer, tinted);
            Assert.All(q, v => Assert.True(v.IsBiomeTinted, $"leaf quad vertex at {v.Position} is unflagged"));
        });
    }
}
