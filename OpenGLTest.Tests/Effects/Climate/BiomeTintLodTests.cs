using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Effects.Climate;

/// <summary>
/// The far LOD's biome tint: tinted LOD vertices wear only tinted tiles, the LOD samples the very
/// tint lattice the chunk mesher does (from the region's own chunk columns, not the prefetch
/// origin one chunk before it), and untinted blocks stay gray.
/// </summary>
public sealed class BiomeTintLodTests
{
    private const int Level = 0;

    public BiomeTintLodTests() => TestContent.EnsureLoaded();

    private static BlockType Grass => ContentRegistry.RequireBlock("grass");

    private static BiomeTintFaces GrassTint =>
        Grass.BiomeTint ?? throw new InvalidOperationException("grass declares no biomeTint");

    private static int RegionChunkSpan => LodRegion.Levels[Level].RegionBlocks / Chunk.Size;

    private static int FloorDiv(int a, int b) => (a >= 0 ? a : a - (b - 1)) / b;

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    private static int Face(Vertex v) => v.Normal & 7;

    /// <summary>Whether the vertex rgb is shade * <paramref name="tint"/> for some AO level.</summary>
    private static bool MatchesSomeShade(Vertex v, int face, Vector3 tint)
    {
        var (vr, vg, vb) = Rgb(v);
        for (var ao = 0; ao < 4; ao++)
        {
            BiomeTint.Shade(ChunkFaceTables.ShadeLut[(face << 2) | ao], tint, out var r, out var g, out var b);
            if (Math.Abs(vr - r) <= 1 && Math.Abs(vg - g) <= 1 && Math.Abs(vb - b) <= 1) return true;
        }
        return false;
    }

    /// <summary>
    /// Level-0 regions whose first chunk-center probe is <paramref name="biome"/> on a non-beach
    /// column, nearest the origin first, each region once.
    /// </summary>
    private static IEnumerable<(int Rx, int Rz)> RegionsOver(Biome biome)
    {
        var seen = new HashSet<(int, int)>();
        for (var ring = 0; ring <= 64; ring++)
        for (var cx = -ring; cx <= ring; cx++)
        for (var cz = -ring; cz <= ring; cz++)
        {
            if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != ring) continue;
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != biome) continue;
            var height = ColumnCache.GetHeights(cx, cz)[(8 << Chunk.SizeShift) | 8];
            if (TerrainGenerator.IsBeachHeight(height)) continue;

            var region = (FloorDiv(cx, RegionChunkSpan), FloorDiv(cz, RegionChunkSpan));
            if (seen.Add(region)) yield return region;
        }
    }

    /// <summary>The chunk mesher's grass tint at a world position: the owning chunk's field.</summary>
    private static Vector3 ChunkMesherTint(BiomeTintPalette palette, float wx, float wz, int shiftChunks = 0)
    {
        var cx = FloorDiv((int)MathF.Floor(wx), Chunk.Size);
        var cz = FloorDiv((int)MathF.Floor(wz), Chunk.Size);
        return BiomeTint.FieldFor(palette, cx - shiftChunks, cz - shiftChunks)
            .Sample(wx - cx * Chunk.Size, wz - cz * Chunk.Size);
    }

    private static LodMeshData FirstForestRegionWithGrass(out int rx, out int rz)
    {
        foreach (var (x, z) in RegionsOver(Biome.Forest))
        {
            var mesh = LodRegion.BuildMeshData(Level, x, z);
            if (!mesh.Vertices.Any(v => v.Layer == GrassTint.Tinted[4])) continue;
            rx = x;
            rz = z;
            return mesh;
        }

        throw new InvalidOperationException("no level-0 forest region with grass tops found");
    }

    [Fact]
    public void TintedLodVerticesWearOnlyTintedTiles()
    {
        var mesh = FirstForestRegionWithGrass(out _, out _);
        var tintedTiles = BlockType.ById
            .Where(b => b?.BiomeTint != null)
            .SelectMany(b => Enumerable.Range(0, 6).Where(b!.BiomeTint!.IsTinted).Select(f => b!.BiomeTint!.Tinted[f]))
            .ToHashSet();
        var grassTinted = Enumerable.Range(0, 6).Where(GrassTint.IsTinted).Select(f => GrassTint.Tinted[f]).ToHashSet();

        foreach (var v in mesh.Vertices.Concat(mesh.WaterVertices))
        {
            if (v.IsBiomeTinted)
                Assert.Contains(v.Layer, tintedTiles);
            if (grassTinted.Contains(v.Layer))
                Assert.True(v.IsBiomeTinted, $"a vertex wearing grass's tinted tile {v.Layer} is unflagged");
        }
    }

    [Fact]
    public void LodTintMatchesTheChunkMesherField()
    {
        var regionBlocks = LodRegion.Levels[Level].RegionBlocks;
        var discriminating = 0;
        var checkedVertices = 0;
        foreach (var (rx, rz) in RegionsOver(Biome.Forest))
        {
            var mesh = LodRegion.BuildMeshData(Level, rx, rz);
            foreach (var v in mesh.Vertices)
            {
                if (v.Layer != GrassTint.Tinted[4] || Face(v) != 4) continue;
                Assert.True(v.IsBiomeTinted);

                var wx = rx * regionBlocks + v.Position.X;
                var wz = rz * regionBlocks + v.Position.Z;
                var expected = ChunkMesherTint(BiomeTintPalette.Grass, wx, wz);
                Assert.True(MatchesSomeShade(v, 4, expected),
                    $"LOD grass at ({wx}, {wz}) is {Rgb(v)}, not shade * the chunk mesher's tint {expected}");
                checkedVertices++;

                // A lattice read one chunk off (the prefetch origin) would have produced this
                // tint instead; count the vertices where that would have been caught.
                if (!MatchesSomeShade(v, 4, ChunkMesherTint(BiomeTintPalette.Grass, wx, wz, shiftChunks: 1)))
                    discriminating++;
            }

            if (discriminating > 0) break;
        }

        Assert.True(checkedVertices > 0, "no tinted LOD grass top found");
        Assert.True(discriminating > 0, "no checked vertex would tell a one-chunk lattice shift apart");
    }

    [Fact]
    public void UntintedLodBlocksStayGray()
    {
        var sand = ContentRegistry.RequireBlock("sand");
        Assert.Null(sand.BiomeTint);
        var sandTiles = sand.FaceTiles.ToHashSet();

        var (rx, rz) = RegionsOver(Biome.Desert).First();
        var mesh = LodRegion.BuildMeshData(Level, rx, rz);
        Assert.Contains(mesh.Vertices, v => sandTiles.Contains(v.Layer));

        foreach (var v in mesh.Vertices.Concat(mesh.WaterVertices))
        {
            if (v.IsBiomeTinted) continue;
            var (r, g, b) = Rgb(v);
            Assert.True(r == g && g == b, $"untinted LOD vertex on tile {v.Layer} is colored {Rgb(v)}");
        }

        Assert.DoesNotContain(mesh.Vertices, v => v.IsBiomeTinted && sandTiles.Contains(v.Layer));
    }

    [Fact]
    public void LodCanopyCarriesTheFoliageField()
    {
        var foliageBlocks = BlockType.ById
            .Where(b => b?.BiomeTint is { Palette: BiomeTintPalette.Foliage })
            .Select(b => b!)
            .ToList();
        Assert.NotEmpty(foliageBlocks);
        var leafTiles = foliageBlocks
            .SelectMany(b => Enumerable.Range(0, 6).Where(b.BiomeTint!.IsTinted).Select(f => b.BiomeTint!.Tinted[f]))
            .ToHashSet();

        var regionBlocks = LodRegion.Levels[Level].RegionBlocks;
        var checkedTrees = 0;
        var discriminating = 0;
        foreach (var (rx, rz) in RegionsOver(Biome.Forest).Take(64))
        {
            var mesh = LodRegion.BuildMeshData(Level, rx, rz);
            foreach (var tree in mesh.Trees.Instances)
            {
                var x = tree.RootX - rx * regionBlocks + 0.5f;
                var z = tree.RootZ - rz * regionBlocks + 0.5f;
                if (!tree.HasCrown || x < 0 || z < 0 || x >= regionBlocks || z >= regionBlocks)
                    continue;
                var material = mesh.Trees.Materials[tree.LeafMaterial];
                if (!Enumerable.Range(0, 6).Any(face => leafTiles.Contains(material.TintedAt(face))))
                    continue;
                Assert.Equal(BiomeTintPalette.Foliage, material.Palette);
                var tint = Assert.IsType<LodTreeTintLattice>(mesh.Trees.Tint);
                var wx = tree.RootX + 0.5f;
                var wz = tree.RootZ + 0.5f;
                var foliage = ChunkMesherTint(BiomeTintPalette.Foliage, wx, wz);
                var grass = ChunkMesherTint(BiomeTintPalette.Grass, wx, wz);
                var actual = tint.Sample(BiomeTintPalette.Foliage, x, z);
                Assert.True(Vector3.Distance(actual, foliage) <= 0.00001f,
                    $"LOD tree at ({wx}, {wz}) wears {actual}, not the chunk foliage field {foliage}");
                checkedTrees++;
                if (MathF.Max(MathF.Abs(foliage.X - grass.X),
                        MathF.Max(MathF.Abs(foliage.Y - grass.Y), MathF.Abs(foliage.Z - grass.Z))) <= 2f / 255f)
                    continue;
                discriminating++;
                Assert.True(Vector3.Distance(actual, grass) > 2f / 255f,
                    $"LOD tree at ({wx}, {wz}) wears the grass palette");
            }

            if (checkedTrees > 0 && discriminating > 0) break;
        }

        Assert.True(checkedTrees > 0, "no level-0 forest region carried a foliage-tinted tree");
        Assert.True(discriminating > 0, "no LOD tree distinguished foliage from grass tint");
    }
}
