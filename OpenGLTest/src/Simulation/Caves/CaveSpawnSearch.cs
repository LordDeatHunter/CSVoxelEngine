using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Simulation.Caves;

/// <summary>
/// What one cave spawn search (<see cref="MobSpawner.TryFindCaveSpawn"/>) looks for: where it rings, how hard it
/// tries, how dark the standing cell must be and which cave biomes may take the spawn.
/// </summary>
/// <param name="MinRadius">The ring's inner radius around the target, in blocks.</param>
/// <param name="MaxRadius">The ring's outer radius around the target, in blocks.</param>
/// <param name="Tries">How many random ring columns are scanned before the search gives up.</param>
/// <param name="SearchHalfHeight">Each column's scan starts at a random height within this many cells of the
/// target's feet.</param>
/// <param name="ScanDepth">How many cells a column is scanned down for a floor under two clear cells.</param>
/// <param name="MaxSkyLight">The most sky light (0..15) the standing cell may hold.</param>
/// <param name="BlockLightLimit">The block light (0..15) at or above which the standing cell counts as torch-lit
/// and is refused (<see cref="MobSpawner.IsLitByBlocks(VoxelEngine.Chunks.ChunkManager, int, int, int, int)"/>).</param>
/// <param name="AllowedBiomes">The cave biomes that may take the spawn, as a mask of
/// <see cref="CaveSpawnRules.Bit(CaveBiome)"/> bits.</param>
public readonly record struct CaveSpawnSearch(
    float MinRadius,
    float MaxRadius,
    int Tries,
    int SearchHalfHeight,
    int ScanDepth,
    int MaxSkyLight,
    int BlockLightLimit,
    int AllowedBiomes);
