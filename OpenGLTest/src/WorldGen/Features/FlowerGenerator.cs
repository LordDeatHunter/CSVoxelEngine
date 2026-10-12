using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Meadow flowers in clumps: one X-shaped cross block sitting on the surface, placed through
/// <see cref="PlantClumps"/> over <see cref="Clumps"/> and gated at the clump's center by
/// <see cref="IsOpenMeadow"/> (open forest), so a meadow grows whole or not at all. A pure
/// function of (x, z) and the seed, so chunks agree without shared state. Only dry grass columns
/// qualify (no beaches, no sea floors), and the cell above the ground must be air over grass.
/// </summary>
public static class FlowerGenerator
{
    private const int ClumpSalt = 0x7A3B1E;

    /// <summary>The clump shape every meadow flower is placed by.</summary>
    public static readonly PlantClumpSpec Clumps = new(
        WorldGenerationConfig.MeadowCellShift,
        WorldGenerationConfig.MeadowRadius,
        WorldGenerationConfig.MeadowJitter,
        WorldGenerationConfig.MeadowMinMembers,
        WorldGenerationConfig.MeadowClumpChance,
        WorldGenerationConfig.MeadowEdgeDensity,
        ClumpSalt);

    // Cached so a membership test never allocates a delegate
    private static readonly Func<int, int, bool> OpennessGate = IsOpenMeadow;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte FlowerBlock;

    private static readonly byte GrassBlock;

    static FlowerGenerator()
    {
        FlowerBlock = ContentRegistry.RequireBlock("flower").Id;
        GrassBlock = ContentRegistry.RequireBlock("grass").Id;
    }

    /// <summary>
    /// Whether a clump centered at (x, z) may grow: forest open enough
    /// (<see cref="TerrainGenerator.ForestOpenness"/> at least
    /// <see cref="WorldGenerationConfig.MeadowMinOpenness"/>), so meadows fill the open stretches
    /// between the woodland instead of the woods themselves.
    /// </summary>
    public static bool IsOpenMeadow(int x, int z) =>
        TerrainGenerator.IsForest(x, z)
        && TerrainGenerator.ForestOpenness(x, z) >= WorldGenerationConfig.MeadowMinOpenness;

    /// <summary>Whether the surface column (x, z) grows a flower, before the terrain checks.</summary>
    public static bool TryGetFlower(int x, int z) =>
        // An oasis disc is a dense meadow whatever the clump rule says
        OasisGenerator.IsDisc(x, z)
        || PlantClumps.TryGetMember(Clumps, WorldGenerationConfig.Seed, x, z, OpennessGate, out _, out _);

    /// <summary>
    /// Stamps flowers onto the surface: one block above the heightmap, into air only, and
    /// only over grass inside this chunk (a carve below leaves the cell empty instead of
    /// floating). Runs on a generation worker thread, after the pebbles.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            // Wet columns and beaches stay bare: flowers are a meadow plant
            if (TerrainGenerator.IsWaterColumn(worldX0 + x, worldZ0 + z, height)) continue;
            if (TerrainGenerator.IsBeachHeight(height)) continue;
            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            if (!TryGetFlower(worldX0 + x, worldZ0 + z)) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            // The ground itself must still be grass: carves, ravine rock and tree trunks
            // growing through the column all disqualify it
            if (ly == 0 || blocks[Chunk.Idx(x, ly - 1, z)] != GrassBlock) continue;
            blocks[i] = FlowerBlock;
        }
    }
}
