using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Wild cotton: clumps of the <c>cotton_plant</c> cross block on the warm, semi-dry savanna
/// before the desert line. Placement is <see cref="PlantClumps"/> over <see cref="Clumps"/> (one
/// candidate clump per coarse cell, a disc of plants thinning outward from its center), gated at
/// the clump's center by <see cref="IsCottonClimate"/> so a clump grows whole or not at all. A pure
/// function of (x, z) and the seed with no noise of its own, so it needs no reseed and chunks agree
/// without shared state. Only a dry grass column with air above takes a plant.
/// </summary>
public static class CottonGenerator
{
    private const int ClumpSalt = 0x2C0770;

    /// <summary>The clump shape every cotton plant is placed by.</summary>
    public static readonly PlantClumpSpec Clumps = new(
        WorldFeatureConfig.CottonCellShift,
        WorldFeatureConfig.CottonRadius,
        WorldFeatureConfig.CottonJitter,
        WorldFeatureConfig.CottonMinMembers,
        WorldFeatureConfig.CottonClumpChance,
        WorldFeatureConfig.CottonEdgeDensity,
        ClumpSalt);

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort CottonBlock;

    private static readonly ushort GrassBlock;

    // Cached so a membership test never allocates a delegate
    private static readonly Func<int, int, bool> ClimateGate = IsCottonClimate;

    static CottonGenerator()
    {
        CottonBlock = ContentRegistry.RequireBlock("cotton_plant").Id;
        GrassBlock = ContentRegistry.RequireBlock("grass").Id;
    }

    /// <summary>
    /// Whether a clump centered at (x, z) may grow: savanna (warm and semi-dry) whose desert weight
    /// (<see cref="TerrainGenerator.BiomeWeights"/>, exactly 0.5 on the desert line) stays under
    /// <see cref="WorldFeatureConfig.CottonMaxDesertWeight"/>, where the savanna/sand border dither
    /// begins, so a clump never lands on dithered sand. Everywhere else stays bare.
    /// </summary>
    public static bool IsCottonClimate(int x, int z) =>
        TerrainGenerator.BiomeWeights(x, z, out _, out var desertWeight) == Biome.Savanna
        && desertWeight < WorldFeatureConfig.CottonMaxDesertWeight;

    /// <summary>Whether the surface column (x, z) is a cotton clump member, before the terrain checks.</summary>
    public static bool TryGetCotton(int x, int z) =>
        PlantClumps.TryGetMember(Clumps, WorldGenerationConfig.Seed, x, z, ClimateGate, out _, out _);

    /// <summary>
    /// Stamps cotton onto the surface: one block above the heightmap, into air only, and only over
    /// grass inside this chunk (a carve below leaves the cell empty instead of floating). Runs on a
    /// generation worker thread after the trees and pebbles, so it never displaces either, and before
    /// the flowers and foliage, so their scatter cannot eat into a clump.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            var ly = height + 1 - worldY0;
            // ly == 0 puts the ground in the chunk below, out of reach of a pure per-chunk stamp
            if (ly < 1 || ly >= Chunk.Size) continue;
            if (blocks[Chunk.Idx(x, ly, z)] != BlockType.Air) continue;
            // The ground itself must still be grass: carves, ravine rock, dithered sand and tree
            // trunks all disqualify it
            if (blocks[Chunk.Idx(x, ly - 1, z)] != GrassBlock) continue;
            // Wet columns and beaches stay bare: cotton is a dry-land plant. The cheap tests run
            // first; the water level is a noise read, so it waits for a column that is a member.
            if (TerrainGenerator.IsBeachHeight(height)) continue;
            if (!TryGetCotton(worldX0 + x, worldZ0 + z)) continue;
            if (TerrainGenerator.IsWaterColumn(worldX0 + x, worldZ0 + z, height)) continue;
            blocks[Chunk.Idx(x, ly, z)] = CottonBlock;
        }
    }

    /// <summary>
    /// Every world y <see cref="Stamp"/> can write in this chunk column: it writes only the cell at
    /// <c>ly = height + 1 - worldY0</c>, one above a column height, so the band is
    /// <see cref="StampBand.AboveSurface"/>.
    /// </summary>
    internal static StampBand WriteBand(ColumnData column) => StampBand.AboveSurface(column);
}
