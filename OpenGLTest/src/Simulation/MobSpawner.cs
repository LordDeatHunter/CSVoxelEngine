using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Player;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Hell;
using VoxelEngine.WorldGen.Sky;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation;

/// <summary>
/// The surface spawn search shared by every mob manager: samples a ring around the player and
/// returns the first dry, loaded column of a mob-spawning biome (<see cref="SpawnsMobs"/>: forest,
/// steppe or savanna) with two clear cells above its ground that no torch
/// lights (<see cref="IsLitByBlocks"/>) and that lies outside every battle tower's footprint (whose dark ground floor
/// is the tower skeletons' own). Main thread only (it reads the live chunk map).
/// </summary>
public static class MobSpawner
{
    /// <summary>
    /// Tries up to <paramref name="tries"/> random columns in the <paramref name="minRadius"/>..
    /// <paramref name="maxRadius"/> ring around the player. On success <paramref name="groundY"/>
    /// is the solid ground cell's Y; the caller places its body above it.
    /// </summary>
    public static bool TryFindSurfaceSpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, out int x, out int groundY, out int z) =>
        TryFindSurfaceSpawn(chunks, playerPosition, minRadius, maxRadius, tries, InTowerFootprint,
            out x, out groundY, out z);

    // A battle tower's footprint box: its ground floor is dark ground at the terrain height (BT-10), and its skeletons
    // own it. TowerPlacement.Meets reads the cached site verdicts; the spawn search runs on the main thread of an
    // authoritative world only (a client's mirrors never spawn).
    private static readonly Func<int, int, bool> InTowerFootprint =
        static (x, z) => WorldGenerationConfig.TowersEnabled && TowerPlacement.Meets(x, z, x, z);

    /// <summary><see cref="TryFindSurfaceSpawn(ChunkManager, Vector3, float, float, int, out int, out int, out int)"/>
    /// with the column refusal passed in: a generated column for which <paramref name="blockedColumn"/> holds never
    /// spawns (the test seam; production refuses the tower footprints).</summary>
    internal static bool TryFindSurfaceSpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, Func<int, int, bool> blockedColumn, out int x, out int groundY, out int z)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(playerPosition, minRadius, maxRadius, out x, out z);

            var chunkX = VoxelCoords.WorldToChunk(x);
            var chunkZ = VoxelCoords.WorldToChunk(z);
            var localX = VoxelCoords.WorldToLocal(x);
            var localZ = VoxelCoords.WorldToLocal(z);
            // The world source's column (worldgen by default): its stored water level is exactly
            // what TerrainGenerator.IsWaterColumn evaluates for a generated column
            var source = chunks.WorldSource;
            var column = source.GetColumn(chunkX, chunkZ);
            var i = VoxelCoords.ColumnIndex(localX, localZ);
            var height = column.Heights[i];

            // Surface spawns only: dry ground of a mob-spawning biome with two clear cells above,
            // all of them in loaded chunks (TryGetCell reports unloaded as absent, not as air). A
            // lake bed counts as wet even where it sits above sea level, so a mob never spawns
            // inside a pond.
            if (height < column.WaterLevel[i]) continue;
            // A generated column keeps worldgen's undithered biome (ColumnData.Biomes holds the
            // dithered one); a column the source supplies only has its own biome to go by
            var biome = source.Supplies(chunkX, chunkZ)
                ? column.Biomes[i]
                : TerrainGenerator.BiomeAt(x, z);
            if (!SpawnsMobs(biome)) continue;
            if (!chunks.TryGetCell(x, height, z, out var ground, out _) || !BlockType.IsSolid(ground)) continue;
            if (!chunks.TryGetCell(x, height + 1, z, out var above1, out _) || BlockType.IsSolid(above1)) continue;
            if (!chunks.TryGetCell(x, height + 2, z, out var above2, out _) || BlockType.IsSolid(above2)) continue;
            // Never inside a battle tower: its dark ground floor passes every check above. A column a world source
            // supplies holds no generated tower.
            if (!source.Supplies(chunkX, chunkZ) && blockedColumn(x, z)) continue;
            // Torch-safe ground: never where a lamp or torch lights the cell a mob would stand in
            if (IsLitByBlocks(chunks, x, height + 1, z)) continue;

            groundY = height;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// The biomes whose surface the ring spawn search takes: the temperate and warm grasslands
    /// (forest, steppe, savanna). The desert, the ocean and the snowy taiga and frozen plains spawn nothing.
    /// </summary>
    private static bool SpawnsMobs(Biome biome) => biome is Biome.Forest or Biome.Steppe or Biome.Savanna;

    /// <summary>
    /// The underground counterpart of <see cref="TryFindSurfaceSpawn"/>: up to <paramref name="tries"/>
    /// random columns in the same ring, each scanned downward from a random height within
    /// <paramref name="searchHalfHeight"/> of the player's feet for at most <paramref name="scanDepth"/>
    /// cells, taking the first solid floor with two clear, dry cells above it. The floor must lie at
    /// least <see cref="MobConfig.SlimeUndergroundDepthBlocks"/> under its column's terrain
    /// height (<see cref="SlimeSize.IsUnderground"/>), the cell above it must be dark (sky light at most
    /// <paramref name="maxSkyLight"/>, read like <see cref="IsLitByBlocks"/>; a cell without a published
    /// light snapshot reads as full sun, so it is refused) and not torch-lit, and every cell read must
    /// be loaded. Any biome, but never in hell (<see cref="HellGenerator.InHell"/>), which is the hell
    /// mobs' own ground. On success <paramref name="groundY"/> is the floor cell's Y.
    /// </summary>
    public static bool TryFindCaveSpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, int searchHalfHeight, int scanDepth, int maxSkyLight,
        out int x, out int groundY, out int z)
    {
        var feetY = MathUtils.FloorToInt(playerPosition.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(playerPosition, minRadius, maxRadius, out x, out z);
            var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
            var surface = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];

            var top = feetY + Random.Shared.Next(-searchHalfHeight, searchHalfHeight + 1);
            // The first floor under two clear cells decides this try
            if (!TryScanFloor(chunks, x, top, z, scanDepth, out var y)) continue;
            if (!HellGenerator.InHell(y) && SlimeSize.IsUnderground(y, surface) && !IsLitByBlocks(chunks, x, y + 1, z)
                && (int)MathF.Round(EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 1.5f, z + 0.5f)).Sun * 15f) <= maxSkyLight)
            {
                groundY = y;
                return true;
            }
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// The hell mobs' search: up to <paramref name="tries"/> random columns in the ring around the
    /// player, each scanned down from a random height within <paramref name="searchHalfHeight"/> of the
    /// player's feet for at most <paramref name="scanDepth"/> cells, taking the first open cavern floor
    /// (solid ground under two clear, dry cells, every cell loaded). The floor and both cells above it
    /// must lie in hell (<see cref="HellGenerator.InHell"/>), so nothing is ever found in the overworld or
    /// the barrier band, and the standing cell's block light must stay at or under
    /// <see cref="MobConfig.HellSpawnMaxBlockLight"/> (a lava-lit pool edge or a torched base stays
    /// clear; hell has no skylight). On success <paramref name="groundY"/> is the floor cell's Y.
    /// </summary>
    public static bool TryFindHellSpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, int searchHalfHeight, int scanDepth, out int x, out int groundY, out int z)
    {
        var feetY = MathUtils.FloorToInt(playerPosition.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(playerPosition, minRadius, maxRadius, out x, out z);
            var top = feetY + Random.Shared.Next(-searchHalfHeight, searchHalfHeight + 1);
            if (!TryScanFloor(chunks, x, top, z, scanDepth, out var y)) continue;
            if (!HellGenerator.InHell(y + 2)) continue;
            var light = EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 1.5f, z + 0.5f));
            if ((int)MathF.Round(light.Block * 15f) > MobConfig.HellSpawnMaxBlockLight) continue;

            groundY = y;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>Whether a body standing at height <paramref name="feetY"/> is in the sky islands' band
    /// (<see cref="WorldGenerationConfig.SkyIslandBottomY"/>..<see cref="WorldGenerationConfig.SkyIslandTopY"/>),
    /// the only place the sky mobs spawn around.</summary>
    public static bool InSkyBand(int feetY) =>
        feetY >= WorldGenerationConfig.SkyIslandBottomY && feetY <= WorldGenerationConfig.SkyIslandTopY;

    /// <summary>
    /// The sky mobs' search: up to <paramref name="tries"/> random columns in the ring around the player, each
    /// scanned down from a random height within <paramref name="searchHalfHeight"/> of the player's feet for at
    /// most <paramref name="scanDepth"/> cells, taking the first open floor (solid ground under two clear, dry
    /// cells, every cell loaded). The floor and both cells above it must lie in the sky band
    /// (<see cref="InSkyBand"/>), so neither the cloud sea under the islands nor the overworld or hell is ever
    /// found, the column must lie inside an island zone (<see cref="SkyIslands.ZoneAt"/>), and no torch may
    /// light the standing cell (<see cref="IsLitByBlocks"/>). On success <paramref name="groundY"/> is the
    /// island top's Y.
    /// </summary>
    public static bool TryFindSkySpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, int searchHalfHeight, int scanDepth, out int x, out int groundY, out int z)
    {
        var feetY = MathUtils.FloorToInt(playerPosition.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < tries; attempt++)
        {
            RingColumn(playerPosition, minRadius, maxRadius, out x, out z);
            var top = feetY + Random.Shared.Next(-searchHalfHeight, searchHalfHeight + 1);
            if (!TryScanFloor(chunks, x, top, z, scanDepth, out var y)) continue;
            if (!InSkyBand(y) || !InSkyBand(y + 2)) continue;
            if (!SkyIslands.ZoneAt(x + 0.5f, z + 0.5f).InZone || IsLitByBlocks(chunks, x, y + 1, z)) continue;

            groundY = y;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    // A random column in the minRadius..maxRadius ring around the player
    private static void RingColumn(Vector3 playerPosition, float minRadius, float maxRadius, out int x, out int z)
    {
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var radius = MathUtils.Lerp(minRadius, maxRadius, Random.Shared.NextSingle());
        x = MathUtils.FloorToInt(playerPosition.X + MathF.Cos(angle) * radius);
        z = MathUtils.FloorToInt(playerPosition.Z + MathF.Sin(angle) * radius);
    }

    // Scans column (x, z) down from top for at most scanDepth cells to the first solid floor with two
    // clear, dry cells above it; false at an unloaded cell, or when the scan ends without one.
    private static bool TryScanFloor(ChunkManager chunks, int x, int top, int z, int scanDepth, out int floorY)
    {
        var clearAbove = 0;
        for (var y = top; y > top - scanDepth; y--)
        {
            if (!chunks.TryGetCell(x, y, z, out var id, out _)) break;
            if (BlockType.IsFluid(id))
            {
                clearAbove = 0;
                continue;
            }
            if (!BlockType.IsSolid(id))
            {
                clearAbove++;
                continue;
            }
            if (clearAbove >= 2)
            {
                floorY = y;
                return true;
            }
            clearAbove = 0;
        }

        floorY = 0;
        return false;
    }

    /// <summary>
    /// Whether the block light (torches, lamps, lava) in cell (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) reaches <see cref="MobConfig.MobSpawnBlockLightLimit"/>, which
    /// keeps every ring spawn off it, so a few torches keep a base clear. Read through
    /// <see cref="EntityLight.Sample"/> from the light each chunk publishes with its mesh (a headless
    /// server publishes it too); a cell without one reads as unlit.
    /// </summary>
    public static bool IsLitByBlocks(ChunkManager chunks, int x, int y, int z)
    {
        var light = EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
        return (int)MathF.Round(light.Block * 15f) >= MobConfig.MobSpawnBlockLightLimit;
    }
}
