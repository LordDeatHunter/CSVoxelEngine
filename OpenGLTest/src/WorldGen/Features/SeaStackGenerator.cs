using System;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Sea stacks: rock pillars rising out of the shallow ocean near a coast, decided per
/// 2^<see cref="WorldGenerationConfig.SeaStackCellShift"/>-block cell from a pure hash of
/// (cell, seed). A site raises every column of its disc to a tapered top above the waterline, so
/// the raise is part of the heightmap itself (<see cref="TerrainGenerator.Compute"/>): the far LOD
/// reads the same heights, the columns become land (stone above the beach band, sand at the
/// skirt), and the ravine cut still applies to the pillar like to any other rock.
/// </summary>
public static class SeaStackGenerator
{
    /// <summary>
    /// The stack top for column (x, z) whose land height is <paramref name="height"/> in
    /// <paramref name="biome"/>: the highest site disc reaching it, tapered down from the centre,
    /// or <paramref name="height"/> itself when no shallow-ocean site does. A pure function.
    /// </summary>
    public static int TopAt(int x, int z, int height, Biome biome)
    {
        if (biome != Biome.Ocean) return height;
        if (height > WorldGenerationConfig.SeaLevel - 1
            || height < WorldGenerationConfig.SeaLevel - WorldGenerationConfig.SeaStackShallowDepth)
            return height;

        var radius = WorldGenerationConfig.SeaStackRadius;
        var shift = WorldGenerationConfig.SeaStackCellShift;
        var minCellX = (x - radius) >> shift;
        var maxCellX = (x + radius) >> shift;
        var minCellZ = (z - radius) >> shift;
        var maxCellZ = (z + radius) >> shift;
        var best = height;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetStack(cellX, cellZ, out var centreX, out var centreZ, out var top)) continue;
            var dx = x - centreX;
            var dz = z - centreZ;
            var distanceSquared = dx * dx + dz * dz;
            if (distanceSquared > radius * radius) continue;
            var tapered = top - (int)MathF.Sqrt(distanceSquared);
            if (tapered > best) best = tapered;
        }
        return best;
    }

    /// <summary>
    /// The stack a cell holds: its centre column (jittered inside the cell) and its top height
    /// (above the waterline), or false for the many cells without one.
    /// </summary>
    public static bool TryGetStack(int cellX, int cellZ, out int x, out int z, out int top)
    {
        x = 0;
        z = 0;
        top = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x57AC);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.SeaStackChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.SeaStackCellShift);
        x = (cellX << WorldGenerationConfig.SeaStackCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.SeaStackCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        var tops = WorldGenerationConfig.SeaStackMaxHeight - WorldGenerationConfig.SeaStackMinHeight + 1;
        top = WorldGenerationConfig.SeaLevel + WorldGenerationConfig.SeaStackMinHeight
              + (int)(SplitMix64.Mix(h + 3) % (ulong)tops);
        return true;
    }
}
