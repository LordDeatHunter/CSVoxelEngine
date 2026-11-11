using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>Small prefilled lava bowls with a solid bottom and rim. Every site fits one chunk,
/// so acceptance sees the entire basin without cross-chunk reads or writes. Only a site meeting
/// existing dry cave air opens a lake; deeper sites win the occurrence roll more often.</summary>
internal static class CaveLavaLakes
{
    private const int Shift = WorldGenerationConfig.CaveLavaLakeCellShift;

    internal static float Chance(int y) => MathUtils.Lerp(WorldGenerationConfig.CaveLavaLakeChanceShallow,
        WorldGenerationConfig.CaveLavaLakeChanceDeep, MathUtils.SmoothStep(WorldGenerationConfig.LavaMaxY,
            WorldGenerationConfig.BarrierTopY, y));

    /// <summary>The candidate's local level, radius and depth, when this is its chosen chunk.</summary>
    internal static bool TrySite(int x0, int y0, int z0, out int level, out int radius, out int depth)
    {
        var cx = x0 >> Shift;
        var cy = y0 >> Shift;
        var cz = z0 >> Shift;
        var h = SplitMix64.Hash(cx ^ (cy * 0x9E37), cz, WorldGenerationConfig.Seed ^ 0x5E83);
        level = WorldGenerationConfig.CaveLavaLakeLocalLevel;
        radius = WorldGenerationConfig.CaveLavaLakeMinRadius + (int)(SplitMix64.Mix(h + 4)
            % (ulong)(WorldGenerationConfig.CaveLavaLakeMaxRadius - WorldGenerationConfig.CaveLavaLakeMinRadius + 1));
        depth = WorldGenerationConfig.CaveLavaLakeMinDepth + (int)(SplitMix64.Mix(h + 5)
            % (ulong)(WorldGenerationConfig.CaveLavaLakeMaxDepth - WorldGenerationConfig.CaveLavaLakeMinDepth + 1));
        var targetX = (cx << Shift) + Chunk.Size * (1 + (int)(SplitMix64.Mix(h + 1) & 1));
        var targetY = (cy << Shift) + Chunk.Size * (1 + (int)(SplitMix64.Mix(h + 2) & 1));
        var targetZ = (cz << Shift) + Chunk.Size * (1 + (int)(SplitMix64.Mix(h + 3) & 1));
        var y = y0 + level;
        return x0 == targetX && y0 == targetY && z0 == targetZ
            && y <= WorldGenerationConfig.LavaMaxY && y - depth - 1 > WorldGenerationConfig.BarrierTopY
            && SplitMix64.Unit(h, 0) < Chance(y);
    }

    internal static void Stamp(ushort[] blocks, ColumnData column, int x0, int y0, int z0)
    {
        if (!TrySite(x0, y0, z0, out var level, out var radius, out var depth)) return;
        const int center = Chunk.Size / 2;
        if (blocks[Chunk.Idx(center, level + 1, center)] != BlockType.Air) return;
        var rim = radius + 1;
        // Check the entire footprint before writing. Surface water, near-surface terrain and
        // any fluid contact reject the site rather than creating a mixing waterfall.
        for (var x = center - rim; x <= center + rim; x++)
        for (var z = center - rim; z <= center + rim; z++)
        {
            if (y0 + level + WorldGenerationConfig.CaveLavaLakeAirHeight > column.Heights[VoxelCoords.ColumnIndex(x, z)] - WorldGenerationConfig.CaveLavaLakeBurial)
                return;
            for (var y = level - depth - 1; y <= level + WorldGenerationConfig.CaveLavaLakeAirHeight; y++)
                if (BlockType.IsFluid(blocks[Chunk.Idx(x, y, z)])) return;
        }

        for (var x = center - rim; x <= center + rim; x++)
        for (var z = center - rim; z <= center + rim; z++)
        {
            var dx = x - center;
            var dz = z - center;
            var distance = dx * dx + dz * dz;
            if (distance > rim * rim) continue;
            var inside = distance <= radius * radius;
            var bottom = level - depth + (int)(distance / (float)(radius * radius) * (depth - 1));
            for (var y = level - depth - 1; y <= level + WorldGenerationConfig.CaveLavaLakeAirHeight; y++)
            {
                var i = Chunk.Idx(x, y, z);
                if (y <= level)
                    blocks[i] = inside && y > bottom ? LavaGenerator.LavaBlock : TerrainGenerator.RockBlock;
                else if (inside)
                    blocks[i] = BlockType.Air;
            }
        }
    }
}
