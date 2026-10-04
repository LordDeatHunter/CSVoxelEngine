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
            var height = ColumnCache.GetHeights(chunkX, chunkZ)[VoxelCoords.ColumnIndex(localX, localZ)];

            // Surface spawns only: dry forest ground with two clear cells above, all of them in
            // loaded chunks (TryGetCell reports unloaded as absent, not as air). A lake bed counts
            // as wet even where it sits above sea level, so a mob never spawns inside a pond.
            if (TerrainGenerator.IsWaterColumn(x, z, height)) continue;
            if (!TerrainGenerator.IsForest(x, z)) continue;
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
