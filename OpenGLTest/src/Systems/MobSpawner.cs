using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The surface spawn search shared by every mob manager: samples a ring around the player and
/// returns the first dry, loaded forest column with two clear cells above its ground. Main thread
/// only (it reads the live chunk map).
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

            groundY = height;
            return true;
        }

        x = groundY = z = 0;
        return false;
    }
}
