using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free biome tint: the grass and foliage colour as a pure function of world (x, z). The
/// climate (<see cref="TerrainGenerator.Climate"/>) picks an sRGB colour from a four-corner
/// palette, which is turned into the linear multiplier a vertex carries against the grey
/// textureless tiles. Meshes sample it on the 16-block lattice of chunk-column corners and
/// interpolate bilinearly, so neighbouring chunks agree bit for bit at their shared borders and
/// the tint depends on vertex position only, never on the cell.
/// <para>
/// The lattice climate is evaluated once per chunk column into <see cref="ColumnData"/>
/// (<see cref="ColumnData.CornerWarmth"/>/<see cref="ColumnData.CornerWetness"/>) and reaches the
/// chunk mesher, the mining pop and the debris through an <see cref="IColumnSource"/>
/// (<see cref="FieldsFromNeighbourhood"/>, <see cref="TryFieldsFor"/>,
/// <see cref="TryCellTint"/>), worldgen or streamed from a server. The far LOD and the felled
/// tree's arbitrary origin still evaluate <see cref="TerrainGenerator.Climate"/> directly
/// (<see cref="TintsAt"/>, <see cref="LatticeTints"/>, <see cref="FieldsAt"/>); the stored value
/// and the direct evaluation are the same bits because they are the same call at the same float
/// arguments.
/// </para>
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
    /// Both palettes' vertex tints at world (x, z), evaluating the climate directly. Climate is
    /// the only costly part, so both come out of one evaluation. A real chunk reads the climate
    /// stored per column instead (<see cref="TryFieldsFor"/>).
    /// </summary>
    public static void TintsAt(float worldX, float worldZ, out Vector3 grass, out Vector3 foliage)
    {
        TerrainGenerator.Climate(worldX, worldZ, out var warmth, out var wetness);
        TintsFromClimate(warmth, wetness, out grass, out foliage);
    }

    /// <summary>Both palettes' vertex tints at a given climate: the one palette lookup every
    /// tint path shares, whether the climate was just evaluated or read from a column.</summary>
    public static void TintsFromClimate(float warmth01, float wetness01, out Vector3 grass, out Vector3 foliage)
    {
        grass = VertexTint(PaletteSrgb(BiomeTintPalette.Grass, warmth01, wetness01));
        foliage = VertexTint(PaletteSrgb(BiomeTintPalette.Foliage, warmth01, wetness01));
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

    /// <summary>
    /// A chunk column's fields from a mesh build's resolved 3x3 column neighbourhood:
    /// <paramref name="columns"/> holds chunk column (cx - 1 + dx, cz - 1 + dz) at index
    /// dx * 3 + dz, so the four lattice corners of the centre column are [4] (cx, cz),
    /// [7] (cx + 1, cz), [5] (cx, cz + 1) and [8] (cx + 1, cz + 1). Reads the stored corner
    /// climate, never noise.
    /// </summary>
    public static BiomeTintFields FieldsFromNeighbourhood(ColumnTerrain[] columns) =>
        FieldsFromCorners(columns[4], columns[7], columns[5], columns[8]);

    /// <summary>
    /// Chunk column (chunkX, chunkZ)'s fields from <paramref name="source"/>'s corner climate of
    /// the four columns owning its lattice corners. False when any of the four is not available
    /// (a streamed column that has not arrived); nothing is guessed in its place.
    /// </summary>
    public static bool TryFieldsFor(IColumnSource source, int chunkX, int chunkZ, out BiomeTintFields fields)
    {
        if (!source.TryGet(chunkX, chunkZ, out var c00)
            || !source.TryGet(chunkX + 1, chunkZ, out var c10)
            || !source.TryGet(chunkX, chunkZ + 1, out var c01)
            || !source.TryGet(chunkX + 1, chunkZ + 1, out var c11))
        {
            fields = default;
            return false;
        }

        fields = FieldsFromCorners(c00, c10, c01, c11);
        return true;
    }

    /// <summary>
    /// The tint at the centre of world cell (worldX, worldZ), from its owning chunk's field over
    /// <paramref name="source"/>: what a standalone copy of a block (the mining pop, debris)
    /// wears. False when a corner column is not available.
    /// </summary>
    public static bool TryCellTint(IColumnSource source, BiomeTintPalette palette, int worldX, int worldZ,
        out Vector3 tint)
    {
        // Arithmetic shift floors negatives, so a cell at -1 belongs to chunk -1.
        var cx = worldX >> Chunk.SizeShift;
        var cz = worldZ >> Chunk.SizeShift;
        if (!TryFieldsFor(source, cx, cz, out var fields))
        {
            tint = default;
            return false;
        }

        tint = fields.Field(palette).Sample((worldX & Chunk.SizeMask) + 0.5f,
            (worldZ & Chunk.SizeMask) + 0.5f);
        return true;
    }

    /// <summary>A chunk column's fields over its four lattice corners from worldgen, shared bit
    /// for bit with its neighbours and with <see cref="FieldsAt"/> at the chunk origin.</summary>
    public static BiomeTintFields FieldsFor(int chunkX, int chunkZ) =>
        TryFieldsFor(WorldgenColumnSource.Instance, chunkX, chunkZ, out var fields)
            ? fields
            : throw new InvalidOperationException("the worldgen column source never misses a column");

    /// <summary>One palette's field over chunk column (chunkX, chunkZ), from worldgen.</summary>
    public static BiomeTintField FieldFor(BiomeTintPalette palette, int chunkX, int chunkZ) =>
        FieldsFor(chunkX, chunkZ).Field(palette);

    /// <summary>The tint at the centre of world cell (worldX, worldZ) from worldgen; see
    /// <see cref="TryCellTint"/>.</summary>
    public static Vector3 CellTint(BiomeTintPalette palette, int worldX, int worldZ) =>
        TryCellTint(WorldgenColumnSource.Instance, palette, worldX, worldZ, out var tint)
            ? tint
            : throw new InvalidOperationException("the worldgen column source never misses a column");

    /// <summary>Both palettes' fields over four lattice corners' stored climate, in the corner
    /// order of <see cref="FieldsAt"/>.</summary>
    private static BiomeTintFields FieldsFromCorners(in ColumnTerrain c00, in ColumnTerrain c10,
        in ColumnTerrain c01, in ColumnTerrain c11)
    {
        TintsFromClimate(c00.Warmth, c00.Wetness, out var g00, out var f00);
        TintsFromClimate(c10.Warmth, c10.Wetness, out var g10, out var f10);
        TintsFromClimate(c01.Warmth, c01.Wetness, out var g01, out var f01);
        TintsFromClimate(c11.Warmth, c11.Wetness, out var g11, out var f11);
        return new BiomeTintFields(new BiomeTintField(g00, g10, g01, g11),
            new BiomeTintField(f00, f10, f01, f11));
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
