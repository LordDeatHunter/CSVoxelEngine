using System;

namespace silkgltriangle;

/// <summary>
/// Meteor craters: a rare bowl cut into the land, its floor lined with obsidian — the scar of an
/// old impact. Decided per 2^<see cref="WorldGenerationConfig.MeteorCellShift"/>-block cell from a
/// pure hash of (cell, seed) (the centre column and a radius), so every chunk the bowl crosses
/// stamps the same cells; each column is cut from its own ground down the bowl profile, so a
/// crater on a slope stays a bowl. Only the surface is cut (never fluids), and the far LOD shows
/// the pre-crater height until a column's edits lower it.
/// </summary>
public static class MeteorGenerator
{
    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte ObsidianBlock;

    static MeteorGenerator() => ObsidianBlock = ContentRegistry.RequireBlock("obsidian").Id;

    /// <summary>
    /// The crater a cell holds: its centre column (jittered inside the cell) and radius, or false
    /// for the many cells without one. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetCrater(int cellX, int cellZ, out int x, out int z, out int radius)
    {
        x = 0;
        z = 0;
        radius = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x3E7E);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.MeteorChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.MeteorCellShift);
        x = (cellX << WorldGenerationConfig.MeteorCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.MeteorCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        var radii = WorldGenerationConfig.MeteorMaxRadius - WorldGenerationConfig.MeteorMinRadius + 1;
        radius = WorldGenerationConfig.MeteorMinRadius + (int)(SplitMix64.Mix(h + 3) % (ulong)radii);
        return true;
    }

    /// <summary>
    /// Cuts every crater that reaches this chunk: each column within the radius is cleared from
    /// its ground down the bowl profile, and the cell at the bowl's floor becomes obsidian. Air
    /// and fluids are never cut, and the chunk's border clips the bowl. Runs on a generation
    /// worker thread after the trees.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        var reach = WorldGenerationConfig.MeteorMaxRadius;
        var shift = WorldGenerationConfig.MeteorCellShift;
        var minCellX = (worldX0 - reach) >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1 + reach) >> shift;
        var minCellZ = (worldZ0 - reach) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1 + reach) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetCrater(cellX, cellZ, out var x, out var z, out var radius)) continue;

            for (var dx = -radius; dx <= radius; dx++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                var distance = MathF.Sqrt(dx * dx + dz * dz);
                if (distance > radius) continue;
                var lx = x + dx - worldX0;
                var lz = z + dz - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;

                var ground = column.Heights[(lx << Chunk.SizeShift) | lz];
                var depth = (int)((radius - distance) * WorldGenerationConfig.MeteorBowlSlope);
                for (var d = 0; d < depth; d++)
                {
                    var ly = ground - d - worldY0;
                    if ((uint)ly >= Chunk.Size) continue;
                    var index = Chunk.Idx(lx, ly, lz);
                    if (blocks[index] == BlockType.Air || BlockType.IsFluid(blocks[index])) continue;
                    // The bowl's floor is the last cell the profile cuts
                    blocks[index] = d == depth - 1 ? ObsidianBlock : BlockType.Air;
                }
            }
        }
    }
}
