namespace silkgltriangle;

/// <summary>
/// Occasional ice spikes standing on the barren frozen plains: a single narrow column of ice
/// grown straight up from the ground. Placement follows the tree rule — a rare density roll
/// thinned by lowest-hash spacing — and only the frozen biome qualifies, so a spike never grows in
/// the taiga or on a frozen sea. A pure function of (x, z) like the other generators.
/// </summary>
public static class IceSpikeGenerator
{
    private const int PlacementSalt = 0x1CE5B1;
    private const int ShapeSalt = 0x1CE5B2;

    /// <summary>
    /// Stamps every ice spike whose column can intersect this chunk. Runs on a generation worker
    /// thread after the sea and before the snow, so a spike keeps its own cell and the snow pass
    /// fills only the air around it.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        // The caller gates this on SnowAndIceEnabled, so the body has no toggle of its own.
        const int R = WorldGenerationConfig.IceSpikeReachBlocks;
        var chunkMaxY = worldY0 + Chunk.Size;

        for (var tx = worldX0 - R; tx < worldX0 + Chunk.Size + R; tx++)
        for (var tz = worldZ0 - R; tz < worldZ0 + Chunk.Size + R; tz++)
        {
            if (!TryGetSpike(tx, tz, out var baseY, out var height)) continue;
            if (baseY > chunkMaxY || baseY + height < worldY0) continue;

            for (var y = 0; y < height; y++)
            {
                var lx = tx - worldX0;
                var ly = baseY + y - worldY0;
                var lz = tz - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) continue;

                var i = Chunk.Idx(lx, ly, lz);
                if (blocks[i] == BlockType.Air) blocks[i] = TerrainGenerator.IceBlock;
            }
        }
    }

    /// <summary>
    /// Whether (x, z) grows a spike, and how tall. Placement (density roll + lowest-hash spacing +
    /// frozen-biome ground rules) uses one hash stream; the height uses another.
    /// </summary>
    public static bool TryGetSpike(int x, int z, out int baseY, out int height)
    {
        baseY = 0;
        height = 0;

        var own = PlacementHash(x, z);
        if (own >= WorldGenerationConfig.IceSpikeChance)
            return false;

        var s = WorldGenerationConfig.IceSpikeSpacingRadius;
        for (var dx = -s; dx <= s; dx++)
        for (var dz = -s; dz <= s; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            if (PlacementHash(x + dx, z + dz) < own)
                return false;
        }

        if (TerrainGenerator.BiomeAt(x, z) != Biome.Frozen)
            return false;

        var column = ColumnCache.Get(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
        var li = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z));
        var ground = column.Heights[li];
        if (TerrainGenerator.IsWaterColumn(x, z, ground))
            return false;

        var range = WorldGenerationConfig.IceSpikeMaxHeight - WorldGenerationConfig.IceSpikeMinHeight + 1;
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ ShapeSalt);
        var unit = (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
        height = WorldGenerationConfig.IceSpikeMinHeight + (int)(unit * range);
        baseY = ground + 1;
        return true;
    }

    private static float PlacementHash(int x, int z)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ PlacementSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
