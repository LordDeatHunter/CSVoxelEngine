using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Ocean;

/// <summary>Generated Dynamic sea-floor materials and finite, water-closed coral mounds.</summary>
internal static class OceanHabitats
{
    private const int CoralSiteSalt = 0x4F434F52; // OCOR; distinct from OCOL and OCRK.

    internal static StampBand WriteBand() => new(
        (int)(WorldGenerationConfig.DynamicOceanFloorHeight - WorldGenerationConfig.DynamicTrenchDepth
              - WorldGenerationConfig.DynamicAbyssDepth),
        WorldGenerationConfig.SeaLevel - 1);

    /// <summary>Uses the one fill-owned corner field after rocks, on normal and probe paths.</summary>
    internal static void Stamp(ushort[] blocks, byte[]? state, ColumnData column,
        int worldX0, int worldY0, int worldZ0, in OceanHabitatField field)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || !WorldGenerationConfig.WaterEnabled) return;
        var coral = OceanHabitatBlocks.Coral;
        var rose = OceanHabitatBlocks.Rose;
        var blue = OceanHabitatBlocks.Blue;
        var moss = OceanHabitatBlocks.SeaMoss;
        var glow = OceanHabitatBlocks.GlowMoss;
        var water = TerrainGenerator.SeaBlock;

        // Dress the visible solid h. Rock columns already occupying h+1 keep their host.
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var ci = (x << Chunk.SizeShift) | z;
            var floorY = column.Heights[ci];
            var habitat = field.Classify(true, column.Biomes[ci] == Biome.Ocean,
                column.WaterLevel[ci], floorY, x + 0.5f, z + 0.5f);
            var material = OceanFloorMaterials.Select(habitat, field.Sample(x + 0.5f, z + 0.5f),
                WorldGenerationConfig.SeaLevel - floorY);
            if (material == OceanFloorMaterial.None) continue;
            var host = floorY >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                ? TerrainGenerator.SandBlock
                : floorY >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                    ? TerrainGenerator.GravelBlock : TerrainGenerator.RockBlock;
            var replacement = material switch
            {
                OceanFloorMaterial.Coral => coral,
                OceanFloorMaterial.SeaMoss => moss,
                _ => glow,
            };
            TryDressFloor(blocks, state, x, z, floorY, worldY0, host, water, replacement);
        }

        // The second pass sees the complete supported reef substrate, independent of X/Z order.
        for (var x = 1; x < Chunk.Size - 1; x++)
        for (var z = 1; z < Chunk.Size - 1; z++)
        {
            var floorY = column.Heights[(x << Chunk.SizeShift) | z];
            var ly = floorY - worldY0;
            if (ly < 0 || ly + 3 >= Chunk.Size || floorY + 3 > WorldGenerationConfig.SeaLevel - 2
                || blocks[Chunk.Idx(x, ly, z)] != coral) continue;
            var hash = SplitMix64.Hash(worldX0 + x, worldZ0 + z,
                WorldGenerationConfig.Seed ^ CoralSiteSalt);
            if (SplitMix64.Unit(hash, 0) >= WorldFeatureConfig.OceanCoralMoundChance) continue;
            var color = SplitMix64.Unit(hash, 1) < 0.5f ? rose : blue;
            TryWriteCoralMound(blocks, state, x, z, floorY, worldY0, coral, water, color);
        }
    }

    /// <summary>Whole single-cell floor preflight; the overlying cell must be source water.</summary>
    internal static bool TryDressFloor(ushort[] blocks, byte[]? state, int x, int z,
        int floorY, int worldY0, ushort floorHost, ushort water, ushort replacement)
    {
        var ly = floorY - worldY0;
        if ((uint)x >= Chunk.Size || (uint)z >= Chunk.Size || ly < 0 || ly + 1 >= Chunk.Size
            || floorY < WriteBand().Min || floorY + 1 >= WorldGenerationConfig.SeaLevel)
            return false;
        var floor = Chunk.Idx(x, ly, z);
        var above = Chunk.Idx(x, ly + 1, z);
        if (blocks[floor] != floorHost || state is not null && state[floor] != 0
            || blocks[above] != water || state is not null && state[above] != 0) return false;
        blocks[floor] = replacement;
        if (state is not null) state[floor] = 0;
        return true;
    }

    /// <summary>Preflights all five supports and seven source-water targets before any write.</summary>
    internal static bool TryWriteCoralMound(ushort[] blocks, byte[]? state, int x, int z,
        int floorY, int worldY0, ushort coralHost, ushort water, ushort color)
    {
        var ly = floorY - worldY0;
        if (x <= 0 || x >= Chunk.Size - 1 || z <= 0 || z >= Chunk.Size - 1
            || ly < 0 || ly + 3 >= Chunk.Size || floorY < WriteBand().Min
            || floorY + 3 > WorldGenerationConfig.SeaLevel - 2) return false;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (System.Math.Abs(dx) + System.Math.Abs(dz) > 1) continue;
            var support = Chunk.Idx(x + dx, ly, z + dz);
            var target = Chunk.Idx(x + dx, ly + 1, z + dz);
            if (blocks[support] != coralHost || state is not null && state[support] != 0
                || blocks[target] != water || state is not null && state[target] != 0) return false;
        }
        for (var step = 2; step <= 3; step++)
        {
            var target = Chunk.Idx(x, ly + step, z);
            if (blocks[target] != water || state is not null && state[target] != 0) return false;
        }
        for (var step = 1; step <= 3; step++)
        {
            var center = Chunk.Idx(x, ly + step, z);
            blocks[center] = color;
            if (state is not null) state[center] = 0;
            if (step != 1) continue;
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (System.Math.Abs(dx) + System.Math.Abs(dz) != 1) continue;
                var side = Chunk.Idx(x + dx, ly + 1, z + dz);
                blocks[side] = color;
                if (state is not null) state[side] = 0;
            }
        }
        return true;
    }
}
