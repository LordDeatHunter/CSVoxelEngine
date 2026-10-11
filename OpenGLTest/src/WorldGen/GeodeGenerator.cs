using System;

namespace silkgltriangle;

/// <summary>
/// Geodes: a rare hollow sphere deep in solid rock, its shell lined with crystal and its heart
/// open air — break into one and the cave opens up. Decided per
/// 2^<see cref="WorldGenerationConfig.GeodeCellShift"/>-block cell from a pure hash of (cell, seed)
/// (the centre column, a depth in the band and a radius 2..3), so every chunk the sphere crosses
/// stamps the same cells; only stone is carved or lined, so a geode never eats soil or a cave. The
/// far LOD shows none.
/// </summary>
public static class GeodeGenerator
{
    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte CrystalBlock;

    private static readonly byte StoneBlock;

    static GeodeGenerator()
    {
        CrystalBlock = ContentRegistry.RequireBlock("crystal_block").Id;
        StoneBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>
    /// The geode a cell holds: its centre (jittered inside the cell, its y in the deep band) and
    /// radius, or false for the many cells without one. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetGeode(int cellX, int cellZ, out int x, out int y, out int z, out int radius)
    {
        x = 0;
        y = 0;
        z = 0;
        radius = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x6E0D);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.GeodeChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.GeodeCellShift);
        x = (cellX << WorldGenerationConfig.GeodeCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.GeodeCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        var depths = WorldGenerationConfig.GeodeMaxY - WorldGenerationConfig.GeodeMinY + 1;
        y = WorldGenerationConfig.GeodeMinY + (int)(SplitMix64.Mix(h + 3) % (ulong)depths);
        var radii = WorldGenerationConfig.GeodeMaxRadius - WorldGenerationConfig.GeodeMinRadius + 1;
        radius = WorldGenerationConfig.GeodeMinRadius + (int)(SplitMix64.Mix(h + 4) % (ulong)radii);
        return true;
    }

    /// <summary>
    /// Carves and lines every geode that reaches this chunk: stone inside the radius becomes
    /// crystal on the shell and air in the heart, clipped at the chunk's border. Runs on a
    /// generation worker thread after the carvers.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var reach = WorldGenerationConfig.GeodeMaxRadius;
        var shift = WorldGenerationConfig.GeodeCellShift;
        var minCellX = (worldX0 - reach) >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1 + reach) >> shift;
        var minCellZ = (worldZ0 - reach) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1 + reach) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetGeode(cellX, cellZ, out var x, out var y, out var z, out var radius)) continue;
            if (y > worldY0 + Chunk.Size - 1 || y + radius < worldY0) continue;

            var heart = radius - 1;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                var distanceSquared = dx * dx + dy * dy + dz * dz;
                if (distanceSquared > radius * radius) continue;
                var lx = x + dx - worldX0;
                var ly = y + dy - worldY0;
                var lz = z + dz - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
                var index = Chunk.Idx(lx, ly, lz);
                if (blocks[index] != StoneBlock) continue;
                blocks[index] = distanceSquared <= heart * heart ? BlockType.Air : CrystalBlock;
            }
        }
    }
}
