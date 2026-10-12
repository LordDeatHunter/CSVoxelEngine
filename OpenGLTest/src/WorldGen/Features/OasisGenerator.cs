using System;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Oases: a rare green pocket deep in the desert — a pond ringed by grass, its own palm and denser
/// flora. Decided per 2^<see cref="WorldGenerationConfig.OasisCellShift"/>-block cell from a pure
/// hash of (cell, seed), gated to the deep desert where <see cref="TerrainGenerator.SurfaceWaterGate"/>
/// keeps ordinary lakes out. The pond carves the heightmap and the water level in
/// <see cref="TerrainGenerator.Compute"/>, the ring's columns are marked in
/// <see cref="ColumnData.Oasis"/> (the chunk fill and the far LOD wear grass there), and the flora
/// generators and the palm species read <see cref="IsDisc"/>. The pond's surface comes from the
/// centre's <see cref="TerrainGenerator.LandHeight"/>, so it is one flat level across the disc.
/// </summary>
public static class OasisGenerator
{
    /// <summary>
    /// The oasis a cell holds: its centre column (jittered inside the cell) and the pond's surface
    /// (the centre's land height less one), or false. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetCentre(int cellX, int cellZ, out int centreX, out int centreZ, out int surface)
    {
        centreX = 0;
        centreZ = 0;
        surface = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x0A51);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.OasisChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.OasisCellShift);
        centreX = (cellX << WorldGenerationConfig.OasisCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        centreZ = (cellZ << WorldGenerationConfig.OasisCellShift) + (int)(SplitMix64.Mix(h + 2) % span);

        // The deep desert only: where SurfaceWaterGate keeps ordinary lakes out, so an oasis is
        // the one body of water there
        var biome = TerrainGenerator.BiomeWeights(centreX, centreZ, out _, out var desertWeight);
        if (biome != Biome.Desert) return false;
        if (TerrainGenerator.SurfaceWaterGate(desertWeight) > 0f) return false;

        surface = (int)TerrainGenerator.LandHeight(centreX, centreZ, out _, out _, out _, out _) - 1;
        return true;
    }

    /// <summary>
    /// Whether the column (x, z) is inside an oasis disc, and that oasis's centre and surface.
    /// A pure function of (x, z) and the seed.
    /// </summary>
    public static bool TryGetDisc(int x, int z, out int centreX, out int centreZ, out int surface)
    {
        centreX = 0;
        centreZ = 0;
        surface = 0;
        var radius = WorldGenerationConfig.OasisRadius;
        var shift = WorldGenerationConfig.OasisCellShift;
        var minCellX = (x - radius) >> shift;
        var maxCellX = (x + radius) >> shift;
        var minCellZ = (z - radius) >> shift;
        var maxCellZ = (z + radius) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetCentre(cellX, cellZ, out var cx, out var cz, out var level)) continue;
            var dx = x - cx;
            var dz = z - cz;
            if (dx * dx + dz * dz > radius * radius) continue;
            centreX = cx;
            centreZ = cz;
            surface = level;
            return true;
        }
        return false;
    }

    /// <summary>Whether the column (x, z) is inside an oasis disc.</summary>
    public static bool IsDisc(int x, int z) => TryGetDisc(x, z, out _, out _, out _);

    /// <summary>Whether the column (x, z) is inside the oasis's pond (the inner disc).</summary>
    public static bool IsPond(int x, int z)
    {
        if (!TryGetDisc(x, z, out var cx, out var cz, out _)) return false;
        var dx = x - cx;
        var dz = z - cz;
        return dx * dx + dz * dz <= WorldGenerationConfig.OasisPondRadius * WorldGenerationConfig.OasisPondRadius;
    }
}
