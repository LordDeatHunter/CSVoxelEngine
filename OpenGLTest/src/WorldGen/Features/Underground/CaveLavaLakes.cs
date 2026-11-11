using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>Small lava pits cut into existing dry cave floors. Every site fits one chunk,
/// so acceptance sees the entire basin without cross-chunk reads or writes. Only a site with
/// existing rock containing its bottom and sides opens a lake; deeper sites win the occurrence roll more often.</summary>
internal static class CaveLavaLakes
{
    private const int Shift = WorldGenerationConfig.CaveLavaLakeCellShift;

    internal static float Chance(int y) => MathUtils.Lerp(WorldGenerationConfig.CaveLavaLakeChanceShallow,
        WorldGenerationConfig.CaveLavaLakeChanceDeep, MathUtils.SmoothStep(WorldGenerationConfig.LavaMaxY,
            WorldGenerationConfig.BarrierTopY, y));

    /// <summary>The candidate's preferred local level, maximum radius and depth, when this is its chosen chunk.</summary>
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
        if (!TrySite(x0, y0, z0, out var preferred, out var radius, out var depth)) return;
        // Search actual floors rather than building at the hashed height in cave air. The whole
        // search stays in this chunk; its verdict cannot depend on loaded neighbors or query order.
        var min = depth + 1;
        var max = Chunk.Size - WorldGenerationConfig.CaveLavaLakeAirHeight - 1;
        for (var offset = 0; offset <= max - min; offset++)
        {
            var level = min + (preferred - min + offset) % (max - min + 1);
            if (TryFloorAtLevel(blocks, column, y0, level, radius, depth)) return;
        }
    }

    private static bool TryFloorAtLevel(ushort[] blocks, ColumnData column, int y0, int level, int radius, int depth)
    {
        for (var r = radius; r >= WorldGenerationConfig.CaveLavaLakeMinRadius; r--)
        {
            var rim = r + 1;
            var width = Chunk.Size - 2 * rim;
            for (var dx = 0; dx < width; dx++)
            for (var dz = 0; dz < width; dz++)
            {
                var x = rim + (Chunk.Size / 2 - rim + dx) % width;
                var z = rim + (Chunk.Size / 2 - rim + dz) % width;
                if (!FitsFloor(blocks, column, y0, level, x, z, r, depth)) continue;
                CarvePit(blocks, level, x, z, r, depth);
                return true;
            }
        }
        return false;
    }

    private static bool FitsFloor(ushort[] blocks, ColumnData column, int y0, int level, int centerX, int centerZ, int radius, int depth)
    {
        if (y0 + level > WorldGenerationConfig.LavaMaxY
            || y0 + level - depth - 1 <= WorldGenerationConfig.BarrierTopY) return false;
        var rim = radius + 1;
        var top = level + WorldGenerationConfig.CaveLavaLakeAirHeight;
        for (var x = centerX - rim; x <= centerX + rim; x++)
        for (var z = centerZ - rim; z <= centerZ + rim; z++)
        {
            var distance = (x - centerX) * (x - centerX) + (z - centerZ) * (z - centerZ);
            if (distance > rim * rim) continue;
            if (y0 + top > column.Heights[VoxelCoords.ColumnIndex(x, z)] - WorldGenerationConfig.CaveLavaLakeBurial)
                return false;
            // A conservative intact rock slab provides all walls and the bottom. Air gaps,
            // fluids and non-solid scenery reject; nothing is repaired by adding rock.
            for (var y = level - depth - 1; y <= level; y++)
            {
                var id = blocks[Chunk.Idx(x, y, z)];
                if (!BlockType.IsSolid(id) || !BlockType.Occludes(id) || BlockType.IsFluid(id)) return false;
            }
            for (var y = level + 1; y <= top; y++)
                if (BlockType.IsFluid(blocks[Chunk.Idx(x, y, z)])) return false;
            // The floor may have small humps, but every pool column must open into dry cave
            // air above them. Clearing those humps only removes existing floor material.
            if (distance <= radius * radius && blocks[Chunk.Idx(x, top, z)] != BlockType.Air) return false;
        }
        return true;
    }

    private static void CarvePit(ushort[] blocks, int level, int centerX, int centerZ, int radius, int depth)
    {
        for (var x = centerX - radius; x <= centerX + radius; x++)
        for (var z = centerZ - radius; z <= centerZ + radius; z++)
        {
            var distance = (x - centerX) * (x - centerX) + (z - centerZ) * (z - centerZ);
            if (distance > radius * radius) continue;
            var bottom = level - depth + (int)(distance / (float)(radius * radius) * (depth - 1));
            for (var y = bottom + 1; y <= level; y++)
                blocks[Chunk.Idx(x, y, z)] = LavaGenerator.LavaBlock;
            for (var y = level + 1; y <= level + WorldGenerationConfig.CaveLavaLakeAirHeight; y++)
                blocks[Chunk.Idx(x, y, z)] = BlockType.Air;
        }
    }
}
