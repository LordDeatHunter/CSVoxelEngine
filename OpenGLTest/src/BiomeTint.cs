using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free biome tint: the grass and foliage colour as a pure function of world (x, z). The
/// climate (<see cref="TerrainGenerator.Climate"/>) picks an sRGB colour from a four-corner
/// palette, which is turned into the linear multiplier a vertex carries against the grey
/// textureless tiles. Meshes sample it on the 16-block lattice of chunk-column corners and
/// interpolate bilinearly, so neighbouring chunks agree bit for bit at their shared borders and
/// the tint depends on vertex position only, never on the cell.
/// </summary>
public static class BiomeTint
{
    /// <summary>
    /// Fragment-side multiplier of a biome-tinted face, mirrored by the literal 4.0 in
    /// <c>assets/shaders/test-vertex.glsl</c> (<c>fTintGain</c>). A tinted face's albedo is
    /// texel * vertexRgb * Gain, so a byte rgb can carry a linear multiplier up to 4 against the
    /// grey tiles.
    /// </summary>
    public const float Gain = 4f;

    /// <summary>log2 of the lattice spacing: the 16-block lattice of chunk-column corners.</summary>
    public const int LatticeShift = Chunk.SizeShift;

    /// <summary>The sRGB target colour of <paramref name="palette"/> at the given climate,
    /// bilinear over its four corners (first axis warmth, second wetness).</summary>
    public static Vector3 PaletteSrgb(BiomeTintPalette palette, float warmth01, float wetness01) =>
        palette == BiomeTintPalette.Foliage
            ? Bilinear(WorldGenerationConfig.FoliageColorColdDry, WorldGenerationConfig.FoliageColorWarmDry,
                WorldGenerationConfig.FoliageColorColdWet, WorldGenerationConfig.FoliageColorWarmWet,
                warmth01, wetness01)
            : Bilinear(WorldGenerationConfig.GrassColorColdDry, WorldGenerationConfig.GrassColorWarmDry,
                WorldGenerationConfig.GrassColorColdWet, WorldGenerationConfig.GrassColorWarmWet,
                warmth01, wetness01);

    /// <summary>The 0..1 vertex multiplier that, times <see cref="Gain"/> against the grey
    /// textureless tile, lands a flat fully lit face on <paramref name="targetSrgb"/>.</summary>
    public static Vector3 VertexTint(Vector3 targetSrgb) =>
        Vector3.Clamp(
            ColorSpace.SrgbToLinear(targetSrgb)
            / (ColorSpace.SrgbToLinear(WorldGenerationConfig.BiomeTintTextureGrey) * Gain),
            Vector3.Zero, Vector3.One);

    /// <summary>
    /// Both palettes' vertex tints at world (x, z). Climate is the only costly part, so both come
    /// out of one evaluation; this is the one place the climate is read for a tint.
    /// </summary>
    public static void TintsAt(float worldX, float worldZ, out Vector3 grass, out Vector3 foliage)
    {
        TerrainGenerator.Climate(worldX, worldZ, out var warmth, out var wetness);
        grass = VertexTint(PaletteSrgb(BiomeTintPalette.Grass, warmth, wetness));
        foliage = VertexTint(PaletteSrgb(BiomeTintPalette.Foliage, warmth, wetness));
    }

    /// <summary>Both palettes' tints at lattice point (latticeX, latticeZ), i.e. the minimum
    /// corner of chunk column (latticeX, latticeZ).</summary>
    public static void LatticeTints(int latticeX, int latticeZ, out Vector3 grass, out Vector3 foliage) =>
        TintsAt(latticeX << LatticeShift, latticeZ << LatticeShift, out grass, out foliage);

    /// <summary>One palette's tint at lattice point (latticeX, latticeZ).</summary>
    public static Vector3 LatticeTint(BiomeTintPalette palette, int latticeX, int latticeZ)
    {
        LatticeTints(latticeX, latticeZ, out var grass, out var foliage);
        return palette == BiomeTintPalette.Foliage ? foliage : grass;
    }

    /// <summary>
    /// A 16-block field with its origin at an arbitrary world cell: what a block set meshed in a
    /// scratch chunk away from its real position (the felled tree, TopplingTree.BuildMeshData)
    /// uses, so local 0..16 maps onto the standing footprint. A real chunk uses
    /// <see cref="FieldsFor"/>.
    /// </summary>
    public static BiomeTintFields FieldsAt(int worldX, int worldZ)
    {
        TintsAt(worldX, worldZ, out var g00, out var f00);
        TintsAt(worldX + Chunk.Size, worldZ, out var g10, out var f10);
        TintsAt(worldX, worldZ + Chunk.Size, out var g01, out var f01);
        TintsAt(worldX + Chunk.Size, worldZ + Chunk.Size, out var g11, out var f11);
        return new BiomeTintFields(new BiomeTintField(g00, g10, g01, g11),
            new BiomeTintField(f00, f10, f01, f11));
    }

    /// <summary>A chunk column's fields over its four lattice corners, shared bit for bit with
    /// its neighbours.</summary>
    public static BiomeTintFields FieldsFor(int chunkX, int chunkZ) =>
        FieldsAt(chunkX << LatticeShift, chunkZ << LatticeShift);

    /// <summary>One palette's field over chunk column (chunkX, chunkZ).</summary>
    public static BiomeTintField FieldFor(BiomeTintPalette palette, int chunkX, int chunkZ) =>
        FieldsFor(chunkX, chunkZ).Field(palette);

    /// <summary>The tint at the centre of world cell (worldX, worldZ), from its owning chunk's
    /// field: what a standalone copy of a block (the mining pop, debris) wears.</summary>
    public static Vector3 CellTint(BiomeTintPalette palette, int worldX, int worldZ)
    {
        // Arithmetic shift floors negatives, so a cell at -1 belongs to chunk -1.
        var cx = worldX >> Chunk.SizeShift;
        var cz = worldZ >> Chunk.SizeShift;
        return FieldFor(palette, cx, cz).Sample((worldX & Chunk.SizeMask) + 0.5f,
            (worldZ & Chunk.SizeMask) + 0.5f);
    }

    /// <summary>
    /// Bilinear interpolation of four corners (first index x) as an explicit weighted sum. At
    /// fx = 1 the c00/c01 terms multiply by exactly 0 (and likewise at the other edges), so two
    /// columns sharing lattice points agree bit for bit at their border, which the nested-lerp
    /// form does not guarantee.
    /// </summary>
    public static Vector3 Bilinear(Vector3 c00, Vector3 c10, Vector3 c01, Vector3 c11, float fx, float fz) =>
        c00 * ((1f - fx) * (1f - fz)) + c10 * (fx * (1f - fz))
        + c01 * ((1f - fx) * fz) + c11 * (fx * fz);

    /// <summary>A vertex's tinted rgb from its AO/face shade. The tint is clamped 0..1, so no
    /// channel can overflow; the one rounding every emitter uses.</summary>
    public static void Shade(byte shade, Vector3 tint, out byte r, out byte g, out byte b)
    {
        r = (byte)(shade * tint.X + 0.5f);
        g = (byte)(shade * tint.Y + 0.5f);
        b = (byte)(shade * tint.Z + 0.5f);
    }
}
