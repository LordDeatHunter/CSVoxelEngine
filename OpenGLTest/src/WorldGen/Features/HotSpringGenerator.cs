using System;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Hot springs: a small pond in a cold biome whose water never freezes, steaming where it meets
/// the cold air. Decided per 2^<see cref="WorldGenerationConfig.SpringCellShift"/>-block cell from
/// a pure hash of (cell, seed) and <see cref="TerrainGenerator.ColdWeight"/> at the site's center,
/// so the carve, the freeze exemption, the far LOD's water and the steam vents all read the same
/// site. The pond's surface comes from the center's <see cref="TerrainGenerator.LandHeight"/>, so
/// it is one flat level across the disc instead of following the ground.
/// </summary>
public static class HotSpringGenerator
{
    /// <summary>
    /// Whether the column (x, z) is inside a spring's disc, and the spring's own water level: the
    /// center column's land height less one. A pure function of (x, z) and the seed.
    /// </summary>
    public static bool TryGetSite(int x, int z, out int level)
    {
        level = 0;
        var radius = WorldGenerationConfig.SpringRadius;
        var shift = WorldGenerationConfig.SpringCellShift;
        var minCellX = (x - radius) >> shift;
        var maxCellX = (x + radius) >> shift;
        var minCellZ = (z - radius) >> shift;
        var maxCellZ = (z + radius) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetCenter(cellX, cellZ, out var centerX, out var centerZ)) continue;
            var dx = x - centerX;
            var dz = z - centerZ;
            if (dx * dx + dz * dz > radius * radius) continue;
            level = (int)TerrainGenerator.LandHeight(centerX, centerZ, out _, out _, out _, out _) - 1;
            return true;
        }
        return false;
    }

    /// <summary>Whether the column (x, z) is inside a spring's disc.</summary>
    public static bool IsSite(int x, int z) => TryGetSite(x, z, out _);

    /// <summary>
    /// The spring a cell holds: its center column (jittered inside the cell), gated on the center's
    /// climate being cold enough (<see cref="WorldGenerationConfig.SpringMinCold"/>), or false.
    /// </summary>
    private static bool TryGetCenter(int cellX, int cellZ, out int x, out int z)
    {
        x = 0;
        z = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x5F1D);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.SpringChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.SpringCellShift);
        x = (cellX << WorldGenerationConfig.SpringCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.SpringCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        if (TerrainGenerator.ColdWeight(x, z) < WorldGenerationConfig.SpringMinCold) return false;
        // A spring is a land feature: a cold ocean column would dig the sea floor
        TerrainGenerator.LandHeight(x, z, out var biome, out _, out _, out _);
        return biome != Biome.Ocean;
    }
}
