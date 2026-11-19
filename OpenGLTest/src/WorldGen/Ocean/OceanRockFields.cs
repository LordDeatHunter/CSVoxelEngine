using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Ocean;

/// <summary>Generated-ocean dark rock fields, with whole-column water/host preflight.</summary>
internal static class OceanRockFields
{
    private const int FieldSalt = 0x4F434F4C; // OCOL
    private const int ColumnSalt = 0x4F43524B; // OCRK

    // Dynamic's final ocean floor is no lower than the basin, trench and abyss sum. Rock writes
    // above that floor and below the top two sea cells, so this band includes every possible write.
    internal static StampBand WriteBand() => new(
        (int)(WorldGenerationConfig.DynamicOceanFloorHeight - WorldGenerationConfig.DynamicTrenchDepth
              - WorldGenerationConfig.DynamicAbyssDepth) + 1,
        WorldGenerationConfig.SeaLevel - 2);

    /// <summary>The only generated-column caller; never requested by Compute or a source import.</summary>
    internal static void Stamp(ushort[] blocks, byte[]? state, ColumnData column,
        int worldX0, int worldY0, int worldZ0)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || !WorldGenerationConfig.WaterEnabled) return;
        var relevant = false;
        for (var i = 0; i < column.Biomes.Length; i++)
        {
            if (column.Biomes[i] != Biome.Ocean
                || column.WaterLevel[i] != WorldGenerationConfig.SeaLevel
                || WorldGenerationConfig.SeaLevel - column.Heights[i] < 3) continue;
            relevant = true;
            break;
        }
        if (!relevant) return;

        // Four generated cache reads after the caller's neighborhood scan. Hits are usual, not
        // guaranteed: eviction or concurrency can recompute a corner. No read returns to Compute.
        var cx = worldX0 >> Chunk.SizeShift;
        var cz = worldZ0 >> Chunk.SizeShift;
        var field = new OceanHabitatField(
            ColumnCache.Get(cx, cz).CornerWarmth,
            ColumnCache.Get(cx + 1, cz).CornerWarmth,
            ColumnCache.Get(cx, cz + 1).CornerWarmth,
            ColumnCache.Get(cx + 1, cz + 1).CornerWarmth);
        var water = TerrainGenerator.SeaBlock;
        var stone = TerrainGenerator.RockBlock;
        var darkRock = TerrainGenerator.CharredStoneBlock;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var ci = (x << Chunk.SizeShift) | z;
            var floorY = column.Heights[ci];
            var habitat = field.Classify(true, column.Biomes[ci] == Biome.Ocean,
                column.WaterLevel[ci], floorY, x + 0.5f, z + 0.5f);
            if (habitat is not (OceanHabitat.DeepPlain or OceanHabitat.Abyss)) continue;
            if (!TryCandidate(worldX0 + x, worldZ0 + z, out var height)) continue;
            var floor = floorY >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                ? TerrainGenerator.SandBlock
                : floorY >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                    ? TerrainGenerator.GravelBlock : stone;
            TryWriteColumn(blocks, state, x, z, floorY, worldY0, height, floor, water,
                habitat == OceanHabitat.Abyss ? darkRock : stone);
        }
    }

    private static bool TryCandidate(int x, int z, out int height)
    {
        height = 0;
        var radius = WorldFeatureConfig.OceanRockFieldRadius;
        var shift = WorldFeatureConfig.OceanRockFieldCellShift;
        var span = 1 << shift;
        var inField = false;
        for (var cx = (x - radius) >> shift; cx <= (x + radius) >> shift; cx++)
        for (var cz = (z - radius) >> shift; cz <= (z + radius) >> shift; cz++)
        {
            var h = SplitMix64.Hash(cx, cz, WorldGenerationConfig.Seed ^ FieldSalt);
            if (SplitMix64.Unit(h, 0) >= WorldFeatureConfig.OceanRockFieldChance) continue;
            var centerX = (cx << shift) + (int)(SplitMix64.Mix(h + 1) % (ulong)span);
            var centerZ = (cz << shift) + (int)(SplitMix64.Mix(h + 2) % (ulong)span);
            var dx = (long)x - centerX;
            var dz = (long)z - centerZ;
            if (dx * dx + dz * dz < (long)radius * radius) inField = true;
        }
        if (!inField) return false;
        var column = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ ColumnSalt);
        if (SplitMix64.Unit(column, 0) >= WorldFeatureConfig.OceanRockColumnChance) return false;
        var range = WorldFeatureConfig.OceanRockColumnMaxHeight - WorldFeatureConfig.OceanRockColumnMinHeight + 1;
        height = WorldFeatureConfig.OceanRockColumnMinHeight
                 + (int)(SplitMix64.Mix(column + 1) % (ulong)range);
        return true;
    }

    /// <summary>Checks the entire one-chunk site before changing any cell; explicit ids keep this pure.</summary>
    internal static bool TryWriteColumn(ushort[] blocks, byte[]? state, int x, int z,
        int floorY, int worldY0, int height, ushort floorHost, ushort water, ushort rock)
    {
        var localFloor = floorY - worldY0;
        if ((uint)x >= Chunk.Size || (uint)z >= Chunk.Size || localFloor < 0
            || height < WorldFeatureConfig.OceanRockColumnMinHeight
            || height > WorldFeatureConfig.OceanRockColumnMaxHeight
            || localFloor + height >= Chunk.Size
            || floorY + height > WorldGenerationConfig.SeaLevel - 2)
            return false;
        if (blocks[Chunk.Idx(x, localFloor, z)] != floorHost) return false;
        for (var step = 1; step <= height; step++)
        {
            var index = Chunk.Idx(x, localFloor + step, z);
            if (blocks[index] != water || state is not null && state[index] != 0) return false;
        }
        for (var step = 1; step <= height; step++)
        {
            var index = Chunk.Idx(x, localFloor + step, z);
            blocks[index] = rock;
            if (state is not null) state[index] = 0;
        }
        return true;
    }
}
