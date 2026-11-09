using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Simulation.Caves;

/// <summary>
/// The cave biome of the cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), the standing cell
/// right above a cave floor the spawn search found (<see cref="MobSpawner.TryFindCaveSpawn"/>). Main thread only (it
/// may read the live chunk map's world source); the result must be a pure function of the cell, so a search asked
/// twice about one cell agrees. Production reads <see cref="CaveBiomes.At"/> through the column's stored biome and
/// height; a test injects one to force a biome.
/// </summary>
public delegate CaveBiome CaveBiomeResolver(ChunkManager chunks, int x, int y, int z);
