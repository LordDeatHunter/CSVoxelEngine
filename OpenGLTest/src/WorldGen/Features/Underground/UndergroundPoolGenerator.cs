using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Underground pools: cave floors between the lava band and the sea pond over with water sources
/// where a low-frequency patch mask and a per-cell roll agree — a mirror of
/// <see cref="LavaGenerator"/> above <see cref="WorldGenerationConfig.LavaMaxY"/> and below
/// <see cref="WorldGenerationConfig.SeaLevel"/>, buried under the terrain, so entrances and ravine
/// floors (which open to the sky) stay dry. Generated water is a source, so the fluid wake path
/// handles a breach like the sea's. A pure function of world position; runs on a generation worker
/// thread after the carvers and <c>FillWater</c>.
/// </summary>
public static class UndergroundPoolGenerator
{
    private const int PoolSalt = 0x0C1A12;

    private static FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte WaterBlock;

    static UndergroundPoolGenerator()
    {
        Reseed();
        WaterBlock = ContentRegistry.RequireBlock("water").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => PatchNoise = TerrainGenerator.MakeNoise(94, WorldFeatureConfig.PoolPatchFrequency);

    /// <summary>
    /// Whether the air cell (x, y, z) ponds water: inside the band between the lava and the sea,
    /// buried under the terrain, inside a patch and winning the pool roll.
    /// </summary>
    public static bool HasPool(int x, int y, int z, int surfaceHeight)
    {
        if (y <= WorldGenerationConfig.LavaMaxY) return false;
        if (y > WorldGenerationConfig.SeaLevel) return false;
        if (y > surfaceHeight - WorldFeatureConfig.PoolBurial) return false;
        if (PatchNoise.GetNoise(x, z) <= WorldFeatureConfig.PoolPatchThreshold) return false;
        return UnitHash(x, y, z) < WorldFeatureConfig.PoolChance;
    }

    /// <summary>
    /// Stamps pools onto cave floors: air cells over a solid, non-fluid floor. The bottom layer is
    /// skipped (its floor lives in the chunk below), so ponded water never floats. Every test is
    /// pure, so the order is free: the patch noise waits for a column whose band window
    /// (above the lava, at or below the sea, buried under its surface) overlaps the chunk.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var surfaceHeight = column.Heights[(x << Chunk.SizeShift) | z];
            // Exactly the y the loop below admits: above the bottom layer and the lava band, at or
            // below the chunk top, the sea and the burial line
            var lo = Math.Max(worldY0 + 1, WorldGenerationConfig.LavaMaxY + 1);
            var hi = Math.Min(Math.Min(worldY0 + Chunk.Size - 1, WorldGenerationConfig.SeaLevel),
                surfaceHeight - WorldFeatureConfig.PoolBurial);
            if (hi < lo) continue;
            if (PatchNoise.GetNoise(worldX0 + x, worldZ0 + z) <= WorldFeatureConfig.PoolPatchThreshold)
                continue;

            for (var ly = 1; ly < Chunk.Size; ly++)
            {
                var y = worldY0 + ly;
                if (y > WorldGenerationConfig.SeaLevel) break;
                if (y <= WorldGenerationConfig.LavaMaxY) continue;
                if (y > surfaceHeight - WorldFeatureConfig.PoolBurial) continue;

                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != BlockType.Air) continue;
                var floor = blocks[Chunk.Idx(x, ly - 1, z)];
                if (floor == BlockType.Air || BlockType.IsFluid(floor)) continue;
                if (UnitHash(worldX0 + x, y, worldZ0 + z) >= WorldFeatureConfig.PoolChance) continue;

                blocks[i] = WaterBlock;
            }
        }
    }

    /// <summary>
    /// Every world y <see cref="Stamp"/> can write in a chunk over <paramref name="column"/>: a pool
    /// cell lies above <see cref="WorldGenerationConfig.LavaMaxY"/>, at or below
    /// <see cref="WorldGenerationConfig.SeaLevel"/> and at or below its column's
    /// <c>height - PoolBurial &lt;= MaxHeight - PoolBurial</c>. This also carries the old
    /// above-the-sea early return: a chunk whose floor is over the sea lies outside the band.
    /// </summary>
    internal static StampBand WriteBand(ColumnData column) =>
        new(WorldGenerationConfig.LavaMaxY + 1,
            Math.Min(WorldGenerationConfig.SeaLevel, column.MaxHeight - WorldFeatureConfig.PoolBurial));

    private static float UnitHash(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x ^ (y * 0x9E37), z, WorldGenerationConfig.Seed ^ PoolSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
