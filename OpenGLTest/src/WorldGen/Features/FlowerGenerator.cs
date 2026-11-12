using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Meadow flowers: one X-shaped cross block sitting on the surface. A meadow is a clump placed through
/// <see cref="PlantClumps"/> over <see cref="Clumps"/> and gated at the clump's center by
/// <see cref="IsOpenMeadow"/> (open forest or steppe), so a meadow grows whole or not at all; inside it
/// the flowers are spread out (<see cref="IsSpreadSpot"/>), never bunched. A pure function of (x, z) and
/// the seed, so chunks agree without shared state. Only dry grass columns qualify (no beaches, no sea
/// floors), and the cell above the ground must be air over grass.
/// </summary>
public static class FlowerGenerator
{
    private const int ClumpSalt = 0x7A3B1E;
    private const int SpreadSalt = 0x5B4EAD;

    /// <summary>The clump shape every meadow flower is placed by.</summary>
    public static readonly PlantClumpSpec Clumps = new(
        WorldFeatureConfig.MeadowCellShift,
        WorldFeatureConfig.MeadowRadius,
        WorldFeatureConfig.MeadowJitter,
        WorldFeatureConfig.MeadowMinMembers,
        WorldFeatureConfig.MeadowClumpChance,
        WorldFeatureConfig.MeadowEdgeDensity,
        ClumpSalt);

    // Cached so a membership test never allocates a delegate
    private static readonly Func<int, int, bool> OpennessGate = IsOpenMeadow;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort FlowerBlock;

    private static readonly ushort GrassBlock;

    static FlowerGenerator()
    {
        FlowerBlock = ContentRegistry.RequireBlock("flower").Id;
        GrassBlock = ContentRegistry.RequireBlock("grass").Id;
    }

    /// <summary>
    /// Whether a clump centered at (x, z) may grow: forest or steppe open enough
    /// (<see cref="TerrainGenerator.ForestOpenness"/> at least
    /// <see cref="WorldFeatureConfig.MeadowMinOpenness"/>), so meadows fill the open stretches
    /// between the woodland and the open temperate grassland instead of the woods themselves.
    /// </summary>
    public static bool IsOpenMeadow(int x, int z) =>
        TerrainGenerator.BiomeAt(x, z) is Biome.Forest or Biome.Steppe
        && TerrainGenerator.ForestOpenness(x, z) >= WorldFeatureConfig.MeadowMinOpenness;

    /// <summary>Whether the surface column (x, z) grows a flower, before the terrain checks.</summary>
    public static bool TryGetFlower(int x, int z) =>
        // The spread hash runs first: it rejects most columns before any clump or oasis lookup
        IsSpreadSpot(x, z)
        // An oasis disc is a full meadow whatever the clump rule says
        && (OasisGenerator.IsDisc(x, z)
            || PlantClumps.TryGetMember(Clumps, WorldGenerationConfig.Seed, x, z, OpennessGate, out _, out _));

    /// <summary>
    /// Whether (x, z) is the one spot of its <see cref="WorldFeatureConfig.MeadowFlowerSpacing"/>-square
    /// patch that may hold a flower: each patch rolls <see cref="WorldFeatureConfig.MeadowFlowerChance"/> and
    /// a hashed spot off its last row and column, so two flowers never touch, not even diagonally.
    /// </summary>
    internal static bool IsSpreadSpot(int x, int z)
    {
        const int spacing = WorldFeatureConfig.MeadowFlowerSpacing;
        var patchX = MathUtils.FloorDiv(x, spacing);
        var patchZ = MathUtils.FloorDiv(z, spacing);
        var h = SplitMix64.Hash(patchX, patchZ, WorldGenerationConfig.Seed ^ SpreadSalt);
        if (SplitMix64.Unit(h, 0) >= WorldFeatureConfig.MeadowFlowerChance) return false;

        const int span = spacing - 1;
        return x - patchX * spacing == (int)(SplitMix64.Unit(h, 1) * span)
               && z - patchZ * spacing == (int)(SplitMix64.Unit(h, 2) * span);
    }

    /// <summary>
    /// Stamps flowers onto the surface: one block above the heightmap, into air only, and
    /// only over grass inside this chunk (a carve below leaves the cell empty instead of
    /// floating). Runs on a generation worker thread, after the pebbles.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            var ly = height + 1 - worldY0;
            // ly == 0 puts the ground in the chunk below, out of reach of a pure per-chunk stamp
            if ((uint)ly >= Chunk.Size || ly == 0) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            // The ground itself must still be grass: carves, ravine rock and tree trunks
            // growing through the column all disqualify it
            if (blocks[Chunk.Idx(x, ly - 1, z)] != GrassBlock) continue;
            // Wet columns and beaches stay bare: flowers are a meadow plant. Every test is pure, so
            // the order is free: the cheap tests run first, and the clump membership and the water
            // level, both noise reads, wait for a column they admit.
            if (TerrainGenerator.IsBeachHeight(height)) continue;
            if (!TryGetFlower(worldX0 + x, worldZ0 + z)) continue;
            if (TerrainGenerator.IsWaterColumn(worldX0 + x, worldZ0 + z, height)) continue;
            blocks[i] = FlowerBlock;
        }
    }

    /// <summary>
    /// Every world y <see cref="Stamp"/> can write in this chunk column: it writes only the cell at
    /// <c>ly = height + 1 - worldY0</c>, one above a column height, so the band is
    /// <see cref="StampBand.AboveSurface"/>.
    /// </summary>
    internal static StampBand WriteBand(ColumnData column) => StampBand.AboveSurface(column);
}
