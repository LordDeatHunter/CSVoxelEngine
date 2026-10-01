using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rules of the GL-free biome tint field: continuity across chunk borders (the lattice the
/// meshers share), the climate axes agreeing with the biome classification, the palettes fitting
/// the vertex range the shader gain can carry, and the samplers being views of one field.
/// </summary>
public sealed class BiomeTintTests
{
    public BiomeTintTests() => TestContent.EnsureLoaded();

    private static readonly BiomeTintPalette[] Palettes = [BiomeTintPalette.Grass, BiomeTintPalette.Foliage];

    private static readonly (int X, int Z)[] Chunks =
        [(0, 0), (-1, 0), (0, -1), (-1, -1), (3, -7), (-12, 5), (40, 40), (-63, 18)];

    [Fact]
    public void FieldIsContinuousAcrossChunkBorders()
    {
        foreach (var palette in Palettes)
        foreach (var (cx, cz) in Chunks)
        foreach (var t in new[] { 0f, 5f, 16f })
        {
            var here = BiomeTint.FieldFor(palette, cx, cz);
            Assert.Equal(here.Sample(16f, t), BiomeTint.FieldFor(palette, cx + 1, cz).Sample(0f, t));
            Assert.Equal(here.Sample(t, 16f), BiomeTint.FieldFor(palette, cx, cz + 1).Sample(t, 0f));
        }
    }

    [Fact]
    public void FieldIsDeterministic()
    {
        foreach (var (cx, cz) in Chunks)
            Assert.Equal(BiomeTint.FieldsFor(cx, cz), BiomeTint.FieldsFor(cx, cz));
    }

    [Fact]
    public void EveryPaletteCornerFitsTheVertexRange()
    {
        Vector3[] corners =
        [
            WorldGenerationConfig.GrassColorColdDry, WorldGenerationConfig.GrassColorColdWet,
            WorldGenerationConfig.GrassColorWarmDry, WorldGenerationConfig.GrassColorWarmWet,
            WorldGenerationConfig.FoliageColorColdDry, WorldGenerationConfig.FoliageColorColdWet,
            WorldGenerationConfig.FoliageColorWarmDry, WorldGenerationConfig.FoliageColorWarmWet,
        ];
        var ceiling = ColorSpace.SrgbToLinear(WorldGenerationConfig.BiomeTintTextureGrey) * BiomeTint.Gain;
        foreach (var corner in corners)
        {
            var needed = ColorSpace.SrgbToLinear(corner) / ceiling;
            Assert.True(needed.X <= 1f && needed.Y <= 1f && needed.Z <= 1f,
                $"palette corner {corner * 255f} needs a vertex multiplier {needed} above 1");
        }
    }

    [Fact]
    public void ClimateIsInUnitRange()
    {
        for (var x = -6000; x <= 6000; x += 173)
        for (var z = -6000; z <= 6000; z += 191)
        {
            TerrainGenerator.Climate(x, z, out var warmth, out var wetness);
            Assert.InRange(warmth, 0f, 1f);
            Assert.InRange(wetness, 0f, 1f);
        }
    }

    [Fact]
    public void ClimateAgreesWithTheBiomeClassification()
    {
        var sawDesert = false;
        for (var cx = -64; cx <= 64; cx++)
        for (var cz = -64; cz <= 64; cz++)
        {
            var x = cx * Chunk.Size + 8;
            var z = cz * Chunk.Size + 8;
            var biome = TerrainGenerator.BiomeAt(x, z);
            TerrainGenerator.Climate(x, z, out var warmth, out var wetness);
            switch (biome)
            {
                case Biome.Desert:
                    sawDesert = true;
                    Assert.True(warmth > 0.5f && wetness <= 0.5f, $"desert at ({x}, {z}): {warmth}, {wetness}");
                    break;
                case Biome.Taiga:
                    Assert.True(warmth < 0.5f && wetness >= 0.5f, $"taiga at ({x}, {z}): {warmth}, {wetness}");
                    break;
                case Biome.Frozen:
                    Assert.True(warmth < 0.5f && wetness <= 0.5f, $"frozen at ({x}, {z}): {warmth}, {wetness}");
                    break;
            }
        }
        Assert.True(sawDesert);
    }

    [Fact]
    public void ShadeNeverExceedsTheShade()
    {
        Vector3[] tints =
        [
            Vector3.One, Vector3.Zero, new(0.5f), new(0.999f, 0.001f, 0.25f),
            BiomeTint.LatticeTint(BiomeTintPalette.Grass, 0, 0),
            BiomeTint.LatticeTint(BiomeTintPalette.Foliage, -3, 9),
        ];
        foreach (var tint in tints)
        for (var s = 0; s <= 255; s++)
        {
            BiomeTint.Shade((byte)s, tint, out var r, out var g, out var b);
            Assert.True(r <= s && g <= s && b <= s);
        }
    }

    [Fact]
    public void CellTintIsTheOwningChunksFieldAtTheCellCentre()
    {
        int[] coords = [-33, -17, -16, -15, -1, 0, 1, 15, 16, 17, 47, 48];
        foreach (var palette in Palettes)
        foreach (var wx in coords)
        foreach (var wz in coords)
        {
            var expected = BiomeTint.FieldFor(palette, wx >> 4, wz >> 4)
                .Sample((wx & 15) + 0.5f, (wz & 15) + 0.5f);
            Assert.Equal(expected, BiomeTint.CellTint(palette, wx, wz));
        }
    }

    [Fact]
    public void BothPalettesReadOneClimate()
    {
        foreach (var (lx, lz) in Chunks)
        {
            BiomeTint.LatticeTints(lx, lz, out var grass, out var foliage);
            Assert.Equal(BiomeTint.LatticeTint(BiomeTintPalette.Grass, lx, lz), grass);
            Assert.Equal(BiomeTint.LatticeTint(BiomeTintPalette.Foliage, lx, lz), foliage);
        }
    }

    [Fact]
    public void FieldsForIsFieldsAtTheChunkOrigin()
    {
        foreach (var (cx, cz) in Chunks)
            Assert.Equal(BiomeTint.FieldsAt(cx * 16, cz * 16), BiomeTint.FieldsFor(cx, cz));

        foreach (var (wx, wz) in new[] { (7, -3), (-101, 250), (1234, -77) })
        foreach (var t in new[] { 0f, 5f, 16f })
        {
            Assert.Equal(BiomeTint.FieldsAt(wx, wz).Grass.Sample(16f, t),
                BiomeTint.FieldsAt(wx + 16, wz).Grass.Sample(0f, t));
            Assert.Equal(BiomeTint.FieldsAt(wx, wz).Foliage.Sample(t, 16f),
                BiomeTint.FieldsAt(wx, wz + 16).Foliage.Sample(t, 0f));
        }
    }
}
