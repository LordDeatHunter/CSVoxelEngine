using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation;

/// <summary>
/// The surface spawn search shared by every mob manager: samples a ring around the player and
/// returns the first dry, loaded forest column with two clear cells above its ground that no torch
/// lights (<see cref="IsLitByBlocks"/>). Main thread only (it reads the live chunk map).
/// </summary>
public static class MobSpawner
{
    /// <summary>
    /// Tries up to <paramref name="tries"/> random columns in the <paramref name="minRadius"/>..
    /// <paramref name="maxRadius"/> ring around the player. On success <paramref name="groundY"/>
    /// is the solid ground cell's Y; the caller places its body above it.
    /// </summary>
    public static bool TryFindSurfaceSpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, out int x, out int groundY, out int z)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var radius = MathUtils.Lerp(minRadius, maxRadius, Random.Shared.NextSingle());
            x = MathUtils.FloorToInt(playerPosition.X + MathF.Cos(angle) * radius);
            z = MathUtils.FloorToInt(playerPosition.Z + MathF.Sin(angle) * radius);

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

            // Surface spawns only: dry forest ground with two clear cells above, all of them in
            // loaded chunks (TryGetCell reports unloaded as absent, not as air). A lake bed counts
            // as wet even where it sits above sea level, so a mob never spawns inside a pond.
            if (height < column.WaterLevel[i]) continue;
            // A generated column keeps worldgen's undithered forest test (ColumnData.Biomes holds
            // the dithered biome); a column the source supplies only has its own biome to go by
            var forest = source.Supplies(chunkX, chunkZ)
                ? column.Biomes[i] == Biome.Forest
                : TerrainGenerator.IsForest(x, z);
            if (!forest) continue;
            if (!chunks.TryGetCell(x, height, z, out var ground, out _) || !BlockType.IsSolid(ground)) continue;
            if (!chunks.TryGetCell(x, height + 1, z, out var above1, out _) || BlockType.IsSolid(above1)) continue;
            if (!chunks.TryGetCell(x, height + 2, z, out var above2, out _) || BlockType.IsSolid(above2)) continue;
            // Torch-safe ground: never where a lamp or torch lights the cell a mob would stand in
            if (IsLitByBlocks(chunks, x, height + 1, z)) continue;

            groundY = height;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// The underground counterpart of <see cref="TryFindSurfaceSpawn"/>: up to <paramref name="tries"/>
    /// random columns in the same ring, each scanned downward from a random height within
    /// <paramref name="searchHalfHeight"/> of the player's feet for at most <paramref name="scanDepth"/>
    /// cells, taking the first solid floor with two clear, dry cells above it. The floor must lie at
    /// least <see cref="WorldGenerationConfig.SlimeUndergroundDepthBlocks"/> under its column's terrain
    /// height (<see cref="SlimeSize.IsUnderground"/>), the cell above it must be dark (sky light at most
    /// <paramref name="maxSkyLight"/>, read like <see cref="IsLitByBlocks"/>; a cell without a published
    /// light snapshot reads as full sun, so it is refused) and not torch-lit, and every cell read must
    /// be loaded. Any biome. On success <paramref name="groundY"/> is the floor cell's Y.
    /// </summary>
    public static bool TryFindCaveSpawn(ChunkManager chunks, Vector3 playerPosition, float minRadius,
        float maxRadius, int tries, int searchHalfHeight, int scanDepth, int maxSkyLight,
        out int x, out int groundY, out int z)
    {
        var feetY = MathUtils.FloorToInt(playerPosition.Y - Camera.EyeHeight);
        for (var attempt = 0; attempt < tries; attempt++)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var radius = MathUtils.Lerp(minRadius, maxRadius, Random.Shared.NextSingle());
            x = MathUtils.FloorToInt(playerPosition.X + MathF.Cos(angle) * radius);
            z = MathUtils.FloorToInt(playerPosition.Z + MathF.Sin(angle) * radius);
            var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
            var surface = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];

            var top = feetY + Random.Shared.Next(-searchHalfHeight, searchHalfHeight + 1);
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
                if (clearAbove < 2)
                {
                    clearAbove = 0;
                    continue;
                }

                // The first floor under two clear cells decides this try
                if (SlimeSize.IsUnderground(y, surface) && !IsLitByBlocks(chunks, x, y + 1, z)
                    && (int)MathF.Round(EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 1.5f, z + 0.5f)).Sun * 15f) <= maxSkyLight)
                {
                    groundY = y;
                    return true;
                }
                break;
            }
        }

        x = groundY = z = 0;
        return false;
    }

    /// <summary>
    /// Whether the block light (torches, lamps, lava) in cell (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) reaches <see cref="WorldGenerationConfig.MobSpawnBlockLightLimit"/>, which
    /// keeps every ring spawn off it, so a few torches keep a base clear. Read through
    /// <see cref="EntityLight.Sample"/> from the light each chunk publishes with its mesh (a headless
    /// server publishes it too); a cell without one reads as unlit.
    /// </summary>
    public static bool IsLitByBlocks(ChunkManager chunks, int x, int y, int z)
    {
        var light = EntityLight.Sample(chunks, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
        return (int)MathF.Round(light.Block * 15f) >= WorldGenerationConfig.MobSpawnBlockLightLimit;
    }
}
