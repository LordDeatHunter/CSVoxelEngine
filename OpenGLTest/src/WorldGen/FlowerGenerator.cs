using System;

namespace silkgltriangle;

/// <summary>
/// Meadow flowers in low-frequency patches: one X-shaped cross block sitting on the
/// surface, a pure function of (x, z) like <see cref="PebbleGenerator"/> so chunks agree
/// without shared state. Only dry grass columns qualify (no beaches, no sea floors),
/// and the cell above the ground must be air over grass.
/// </summary>
public static class FlowerGenerator
{
    private const int PatchSalt = 0x7A3B1C;
    private const int DensitySalt = 0x7A3B1D;

    private static readonly FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once, like the noise.</summary>
    public static readonly byte FlowerBlock;

    private static readonly byte GrassBlock;

    static FlowerGenerator()
    {
        PatchNoise = TerrainGenerator.MakeNoise(62, WorldGenerationConfig.FlowerPatchFrequency);
        FlowerBlock = ContentRegistry.RequireBlock("flower").Id;
        GrassBlock = ContentRegistry.RequireBlock("grass").Id;
    }

    /// <summary>Whether the surface column (x, z) grows a flower.</summary>
    public static bool TryGetFlower(int x, int z)
    {
        // A meadow plant: desert and ocean columns stay bare
        if (!TerrainGenerator.IsForest(x, z)) return false;
        if (PatchNoise.GetNoise(x, z) <= WorldGenerationConfig.FlowerPatchThreshold) return false;
        return UnitHash(x, z, DensitySalt) < WorldGenerationConfig.FlowerDensity;
    }

    /// <summary>
    /// Stamps flowers onto the surface: one block above the heightmap, into air only, and
    /// only over grass inside this chunk (a carve below leaves the cell empty instead of
    /// floating). Runs on a generation worker thread, after the pebbles.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            // Wet columns and beaches stay bare: flowers are a meadow plant
            if (TerrainGenerator.IsWaterColumn(worldX0 + x, worldZ0 + z, height)) continue;
            if (TerrainGenerator.IsBeachHeight(height)) continue;
            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            if (!TryGetFlower(worldX0 + x, worldZ0 + z)) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            // The ground itself must still be grass: carves, ravine rock and tree trunks
            // growing through the column all disqualify it
            if (ly == 0 || blocks[Chunk.Idx(x, ly - 1, z)] != GrassBlock) continue;
            blocks[i] = FlowerBlock;
        }
    }

    private static float UnitHash(int x, int z, int salt)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ salt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
