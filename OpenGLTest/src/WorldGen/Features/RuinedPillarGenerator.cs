using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Ruined pillars: a broken stone-brick column of 2-4 cells, some bricks missing, scattered over
/// the dry land as ruins. Decided per 2^<see cref="WorldGenerationConfig.PillarCellShift"/>-block
/// cell from a pure hash of (cell, seed); the center column's ground comes from
/// <see cref="ColumnCache"/> like the pebble scatter's, so a pillar near a chunk border is whole.
/// The far LOD shows none.
/// </summary>
public static class RuinedPillarGenerator
{
    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte BrickBlock;

    static RuinedPillarGenerator() => BrickBlock = ContentRegistry.RequireBlock("stone_bricks").Id;

    /// <summary>
    /// The pillar a cell holds: the column it stands on (jittered inside the cell), its height in
    /// cells (2..4) and a gap bit per level above the bottom (bit 0 = the second cell), or false
    /// for the many cells without one. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetPillar(int cellX, int cellZ, out int x, out int z, out int height, out uint gaps)
    {
        x = 0;
        z = 0;
        height = 0;
        gaps = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x7111);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.PillarChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.PillarCellShift);
        x = (cellX << WorldGenerationConfig.PillarCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.PillarCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        var heights = WorldGenerationConfig.PillarMaxHeight - WorldGenerationConfig.PillarMinHeight + 1;
        height = WorldGenerationConfig.PillarMinHeight + (int)(SplitMix64.Mix(h + 3) % (ulong)heights);
        gaps = (uint)(SplitMix64.Mix(h + 4) & 0xF);
        return true;
    }

    /// <summary>
    /// Stamps every pillar that reaches this chunk: its cells at the center column's ground plus
    /// one, into air only, a level skipped where the gap bit is set, clipped at the chunk's border.
    /// Runs on a generation worker thread after the trees.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var shift = WorldGenerationConfig.PillarCellShift;
        var minCellX = worldX0 >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1) >> shift;
        var minCellZ = worldZ0 >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetPillar(cellX, cellZ, out var x, out var z, out var height, out var gaps)) continue;
            if (x < worldX0 || x >= worldX0 + Chunk.Size || z < worldZ0 || z >= worldZ0 + Chunk.Size) continue;
            var ground = HeightAt(x, z);
            if (TerrainGenerator.IsWaterColumn(x, z, ground)) continue;
            StampPillar(blocks, worldY0, x, z, ground, height, gaps);
        }
    }

    /// <summary>
    /// Writes one pillar at column (x, z) whose ground is <paramref name="ground"/>: bricks from
    /// one above the ground up to <paramref name="height"/> cells, a level skipped where the
    /// corresponding <paramref name="gaps"/> bit is set (the bottom cell always stands), into air
    /// only. The test seam for the shape.
    /// </summary>
    internal static void StampPillar(byte[] blocks, int worldY0, int x, int z, int ground, int height,
        uint gaps)
    {
        var lx = x & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        for (var level = 0; level < height; level++)
        {
            if (level > 0 && (gaps & (1u << (level - 1))) != 0) continue;
            var ly = ground + 1 + level - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            var index = Chunk.Idx(lx, ly, lz);
            if (blocks[index] != BlockType.Air) continue;
            blocks[index] = BrickBlock;
        }
    }

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
            [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
}
