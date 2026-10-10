using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The far LOD over an <see cref="IWorldSource"/>: <see cref="LodRegion.BuildMeshData"/> draws a
/// column the source supplies from the source's own <see cref="ColumnData"/> (its heights, its
/// biome, its stored corner climate and its dirt depth, with no worldgen canopy), and draws every
/// column the source does not supply exactly as worldgen always did.
/// </summary>
[Collection("Worldgen seed")]
public sealed class WorldSourceLodTests : IDisposable
{
    private const int Level = 0;
    private const int TopFace = 4;

    // How far above worldgen's terrain (and anything worldgen draws there) the test plateaus stand.
    private const int PlateauLift = 24;

    // The vertex alpha LodRegion's AddQuad flags opaque LOD foliage (a canopy bump) with; 254 is
    // water and 255 everything else.
    private const uint CanopyAlpha = 253;

    // The interior of a region these tests look at: region-local X and Z from half a chunk into the
    // region's first chunk column to half a chunk into its second. Every cell window, AO and sky
    // read behind a vertex there stays inside those two chunk columns, which every source below
    // supplies, so no generated column can reach it.
    private const float InteriorMin = Chunk.Size / 2f;
    private const float InteriorMax = Chunk.Size + Chunk.Size / 2f;

    public WorldSourceLodTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static LodLevelSpec Spec => LodRegion.Levels[Level];

    private static int RegionChunkSpan => Spec.RegionBlocks / Chunk.Size;

    private static BlockType BlockOf(byte id) => BlockType.ById[id]!;

    private static BiomeTintFaces GrassTint =>
        BlockOf(TerrainGenerator.SurfaceBlock).BiomeTint
        ?? throw new InvalidOperationException("grass declares no biomeTint");

    private static int Face(Vertex v) => v.Normal & 7;

    private static bool IsCanopy(Vertex v) => (v.PackedColor >> 24) == CanopyAlpha;

    private static (byte R, byte G, byte B) Rgb(Vertex v) =>
        ((byte)(v.PackedColor & 0xFF), (byte)((v.PackedColor >> 8) & 0xFF), (byte)((v.PackedColor >> 16) & 0xFF));

    private static bool InInterior(Vertex v) =>
        v.Position.X >= InteriorMin && v.Position.X < InteriorMax
        && v.Position.Z >= InteriorMin && v.Position.Z < InteriorMax;

    /// <summary>Whether the vertex rgb is shade * <paramref name="tint"/> for some AO level.</summary>
    private static bool MatchesSomeShade(Vertex v, int face, System.Numerics.Vector3 tint)
    {
        var (vr, vg, vb) = Rgb(v);
        for (var ao = 0; ao < 4; ao++)
        {
            BiomeTint.Shade(Chunk.ShadeLut[(face << 2) | ao], tint, out var r, out var g, out var b);
            if (Math.Abs(vr - r) <= 1 && Math.Abs(vg - g) <= 1 && Math.Abs(vb - b) <= 1) return true;
        }
        return false;
    }

    private static System.Numerics.Vector3 GrassTintOf(float warmth, float wetness)
    {
        BiomeTint.TintsFromClimate(warmth, wetness, out var grass, out _);
        return grass;
    }

    // Worldgen's tallest column over the chunk columns a source over region (rx, rz) supplies.
    private static int TallestUnder(int rx, int rz)
    {
        var cx = rx * RegionChunkSpan;
        var cz = rz * RegionChunkSpan;
        var tallest = int.MinValue;
        for (var x = cx - 1; x <= cx + 2; x++)
        for (var z = cz - 1; z <= cz + 2; z++)
            tallest = Math.Max(tallest, ColumnCache.Get(x, z).MaxHeight);
        return tallest;
    }

    // Worldgen's lowest column over the same chunk columns.
    private static int LowestUnder(int rx, int rz)
    {
        var cx = rx * RegionChunkSpan;
        var cz = rz * RegionChunkSpan;
        var lowest = int.MaxValue;
        for (var x = cx - 1; x <= cx + 2; x++)
        for (var z = cz - 1; z <= cz + 2; z++)
            lowest = Math.Min(lowest, ColumnCache.Get(x, z).MinHeight);
        return lowest;
    }

    // A flat source over region (rx, rz)'s first two chunk columns on each axis plus the prefetch ring
    // before them, so the interior and everything its cells read is supplied.
    private static FlatWorldSource Over(int rx, int rz, int top, Biome biome, float warmth = 0.5f,
        float wetness = 0.5f, int dirtDepth = WorldGenerationConfig.NominalDirtDepth)
    {
        var cx = rx * RegionChunkSpan;
        var cz = rz * RegionChunkSpan;
        return new FlatWorldSource(cx - 1, cx + 2, cz - 1, cz + 2, top, TerrainGenerator.SandstoneBlock, biome,
            warmth, wetness, dirtDepth: dirtDepth);
    }

    private static List<Vertex> InteriorTops(LodMeshData mesh) =>
        mesh.Vertices.Where(v => InInterior(v) && Face(v) == TopFace).ToList();

    /// <summary>The level-0 region nearest the origin whose worldgen LOD draws a canopy bump in its
    /// interior, with that build.</summary>
    private static (int Rx, int Rz, LodMeshData Mesh) RegionWithInteriorCanopy()
    {
        for (var ring = 0; ring <= 24; ring++)
        for (var rx = -ring; rx <= ring; rx++)
        for (var rz = -ring; rz <= ring; rz++)
        {
            if (Math.Max(Math.Abs(rx), Math.Abs(rz)) != ring) continue;
            var centreX = rx * Spec.RegionBlocks + Chunk.Size;
            var centreZ = rz * Spec.RegionBlocks + Chunk.Size;
            if (TerrainGenerator.BiomeAt(centreX, centreZ) is not (Biome.Forest or Biome.Taiga)) continue;

            var mesh = LodRegion.BuildMeshData(Level, rx, rz);
            if (mesh.Vertices.Any(v => InInterior(v) && IsCanopy(v)))
                return (rx, rz, mesh);
        }

        throw new InvalidOperationException("no level-0 region near the origin draws a canopy in its interior");
    }

    private static void AssertSameMesh(LodMeshData expected, LodMeshData actual)
    {
        // SequenceEqual, not Assert.Equal: Vertex is a struct of float-bearing fields
        Assert.True(expected.Vertices.SequenceEqual(actual.Vertices), "the vertices differ");
        Assert.True(expected.Indices.SequenceEqual(actual.Indices), "the indices differ");
        Assert.True(expected.WaterVertices.SequenceEqual(actual.WaterVertices), "the water vertices differ");
        Assert.True(expected.WaterIndices.SequenceEqual(actual.WaterIndices), "the water indices differ");
    }

    [Fact]
    public void ASuppliedColumnsFarLodFollowsItsOwnHeightsAndBiome()
    {
        // A region whose worldgen LOD has a tree there, so dropping the canopy is a real change
        var (rx, rz, generated) = RegionWithInteriorCanopy();
        var generatedInterior = generated.Vertices.Where(InInterior).ToList();
        var generatedMaxY = generatedInterior.Max(v => v.Position.Y);

        // A dry desert plateau above worldgen's terrain and every canopy it draws there
        var top = Math.Max(TallestUnder(rx, rz), (int)MathF.Ceiling(generatedMaxY)) + PlateauLift;
        var source = Over(rx, rz, top, Biome.Desert, warmth: 0.95f, wetness: 0.05f);
        var supplied = LodRegion.BuildMeshData(Level, rx, rz, source: source);
        var interior = supplied.Vertices.Where(InInterior).ToList();
        Assert.NotEmpty(interior);

        // The shell rose to the import's terrain and draws no worldgen tree on it
        Assert.True(interior.Max(v => v.Position.Y) > generatedMaxY,
            "the supplied region's far LOD did not rise to the import's terrain");
        Assert.DoesNotContain(interior, IsCanopy);
        Assert.DoesNotContain(supplied.WaterVertices, InInterior);

        // Every top face stands within a cell of the plateau's own top face, lowered by the level's
        // bias, and wears the desert's sand: the supplied biome decides the tile, where worldgen's
        // region wore something else
        var sandTop = BlockOf(TerrainGenerator.SandBlock).FaceTiles[TopFace];
        var tops = InteriorTops(supplied);
        Assert.NotEmpty(tops);
        foreach (var v in tops)
        {
            Assert.Equal(sandTop, v.Layer);
            Assert.InRange(v.Position.Y, top + 1 - Spec.CellSize - Spec.HeightBias, top + 1);
        }

        Assert.Contains(InteriorTops(generated), v => v.Layer != sandTop);

        // A basin sunk under worldgen's terrain: worldgen's trees there would stand far above it,
        // and none is drawn - a supplied column's trees are in its own blocks
        var basin = LowestUnder(rx, rz) - PlateauLift;
        var sunk = LodRegion.BuildMeshData(Level, rx, rz,
                source: Over(rx, rz, basin, Biome.Desert, warmth: 0.95f, wetness: 0.05f))
            .Vertices.Where(InInterior).ToList();
        Assert.NotEmpty(sunk);
        Assert.DoesNotContain(sunk, IsCanopy);
        Assert.All(sunk, v => Assert.True(v.Position.Y <= basin + 1,
            $"a vertex at {v.Position} stands above the import's basin at {basin}"));
    }

    [Fact]
    public void ASuppliedColumnsTintComesFromItsStoredClimate()
    {
        const int rx = 0;
        const int rz = 0;
        const float wetness = 0.5f;
        const float coldWarmth = 0.05f;
        const float warmWarmth = 0.95f;
        var top = TallestUnder(rx, rz) + PlateauLift;
        var cold = LodRegion.BuildMeshData(Level, rx, rz, source: Over(rx, rz, top, Biome.Forest, coldWarmth, wetness));
        var warm = LodRegion.BuildMeshData(Level, rx, rz, source: Over(rx, rz, top, Biome.Forest, warmWarmth, wetness));

        // The two sources differ only in their stored climate, so the meshes differ only in colour
        Assert.Equal(cold.Vertices.Count, warm.Vertices.Count);
        var compared = 0;
        for (var i = 0; i < cold.Vertices.Count; i++)
        {
            var c = cold.Vertices[i];
            var w = warm.Vertices[i];
            Assert.Equal(c.Position, w.Position);
            Assert.Equal(c.Layer, w.Layer);
            if (!InInterior(c) || Face(c) != TopFace || !c.IsBiomeTinted) continue;

            Assert.True(w.IsBiomeTinted);
            Assert.True(MatchesSomeShade(c, TopFace, GrassTintOf(coldWarmth, wetness)),
                $"the cold import's grass at {c.Position} is {Rgb(c)}, not shade * its stored climate's tint");
            Assert.True(MatchesSomeShade(w, TopFace, GrassTintOf(warmWarmth, wetness)),
                $"the warm import's grass at {w.Position} is {Rgb(w)}, not shade * its stored climate's tint");
            Assert.NotEqual(Rgb(c), Rgb(w));
            compared++;
        }

        Assert.True(compared > 0, "no biome-tinted grass top in the supplied interior");
    }

    [Fact]
    public void AColumnTheSourceDoesNotSupplyKeepsItsWorldgenLod()
    {
        const int rx = 0;
        const int rz = 0;
        var generated = LodRegion.BuildMeshData(Level, rx, rz);

        // The default source is the sourceless build, bit for bit
        AssertSameMesh(generated, LodRegion.BuildMeshData(Level, rx, rz, source: GeneratedWorldSource.Instance));

        // A source whose columns lie far off the region supplies nothing it samples
        var top = TallestUnder(rx, rz) + PlateauLift;
        var far = new FlatWorldSource(1000, 1001, 1000, 1001, top, TerrainGenerator.SandstoneBlock, Biome.Desert,
            0.95f, 0.05f);
        AssertSameMesh(generated, LodRegion.BuildMeshData(Level, rx, rz, source: far));

        // A source supplying part of the region changes it, but the cells that sample only its
        // last chunk column on X, which the source does not supply, stay worldgen's: from half a
        // chunk into that column, no cell window, AO or sky read reaches a supplied column
        var mixed = LodRegion.BuildMeshData(Level, rx, rz, source: Over(rx, rz, top, Biome.Desert));
        Assert.False(generated.Vertices.SequenceEqual(mixed.Vertices), "the supplied columns changed nothing");

        var unsuppliedFrom = Spec.RegionBlocks - Chunk.Size / 2f;
        bool Unsupplied(Vertex v) => v.Position.X >= unsuppliedFrom;
        Assert.Contains(generated.Vertices, Unsupplied);
        Assert.True(generated.Vertices.Where(Unsupplied).SequenceEqual(mixed.Vertices.Where(Unsupplied)),
            "a cell that samples only unsupplied columns changed");
        Assert.True(generated.WaterVertices.Where(Unsupplied).SequenceEqual(mixed.WaterVertices.Where(Unsupplied)),
            "the water of a cell that samples only unsupplied columns changed");
    }

    [Fact]
    public void ASuppliedColumnsBareRockComesFromItsDirtDepthNotItsHeight()
    {
        const int rx = 0;
        const int rz = 0;

        // Above the rock line, where a worldgen forest column wears bare stone
        var top = Math.Max(TallestUnder(rx, rz), (int)WorldGenerationConfig.MountainRockHeight) + PlateauLift;
        var stoneTop = BlockOf(TerrainGenerator.RockBlock).FaceTiles[TopFace];
        var grassTop = GrassTint.Tinted[TopFace];

        // A soiled import keeps its grass however high it stands
        var soiled = InteriorTops(LodRegion.BuildMeshData(Level, rx, rz, source: Over(rx, rz, top, Biome.Forest)));
        Assert.Contains(soiled, v => v.Layer == grassTop);
        Assert.DoesNotContain(soiled, v => v.Layer == stoneTop);

        // The same import with no dirt is bare rock
        var bare = InteriorTops(LodRegion.BuildMeshData(Level, rx, rz,
            source: Over(rx, rz, top, Biome.Forest, dirtDepth: 0)));
        Assert.NotEmpty(bare);
        Assert.All(bare, v => Assert.Equal(stoneTop, v.Layer));
    }
}
