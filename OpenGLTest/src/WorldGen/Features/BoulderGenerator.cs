using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Boulders: a rare blob of stone or cobblestone cells sitting on the forest or taiga floor,
/// decided per 2^<see cref="WorldGenerationConfig.BoulderCellShift"/>-block cell from a pure hash
/// of (cell, seed), so every chunk a boulder reaches stamps the same cells (clipped at its
/// border). Its cells share one height (the center column's ground plus one) and go into air only;
/// the far LOD shows none.
/// </summary>
public static class BoulderGenerator
{
    private const ulong AcceptStream = 0x9E3779B97F4A7C15UL;
    private const ulong CenterXStream = unchecked(AcceptStream * 2);
    private const ulong CenterZStream = unchecked(AcceptStream * 3);
    private const ulong CountStream = unchecked(AcceptStream * 4);
    private const ulong ShapeStream = unchecked(AcceptStream * 5);
    private const ulong MaterialStream = unchecked(AcceptStream * 6);

    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte StoneBlock;
    public static readonly byte CobblestoneBlock;

    static BoulderGenerator()
    {
        StoneBlock = ContentRegistry.RequireBlock("stone").Id;
        CobblestoneBlock = ContentRegistry.RequireBlock("cobblestone").Id;
    }

    /// <summary>
    /// The boulder a cell holds: its center column (jittered inside the cell), its block count
    /// (2..4) and its material, or false for the many cells without one. A pure function of the
    /// cell and the seed.
    /// </summary>
    public static bool TryGetBoulder(int cellX, int cellZ, out int centerX, out int centerZ, out int count,
        out byte blockId)
    {
        centerX = centerZ = 0;
        count = 0;
        blockId = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x80D1);
        if (Unit(SplitMix64.Mix(h + AcceptStream)) >= WorldGenerationConfig.BoulderChance) return false;

        var size = 1 << WorldGenerationConfig.BoulderCellShift;
        var span = (ulong)size;
        centerX = (cellX << WorldGenerationConfig.BoulderCellShift) + (int)(SplitMix64.Mix(h + CenterXStream) % span);
        centerZ = (cellZ << WorldGenerationConfig.BoulderCellShift) + (int)(SplitMix64.Mix(h + CenterZStream) % span);
        var counts = WorldGenerationConfig.BoulderMaxBlocks - WorldGenerationConfig.BoulderMinBlocks + 1;
        count = WorldGenerationConfig.BoulderMinBlocks + (int)(SplitMix64.Mix(h + CountStream) % (ulong)counts);
        blockId = (SplitMix64.Mix(h + MaterialStream) & 1) == 0 ? StoneBlock : CobblestoneBlock;
        return true;
    }

    /// <summary>
    /// Stamps every boulder that reaches this chunk: its cells around the center at the center
    /// column's ground plus one, into air only, clipped at the chunk's border. Runs on a generation
    /// worker thread after the trees, so a boulder never displaces one.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var shift = WorldGenerationConfig.BoulderCellShift;
        var minCellX = (worldX0 - 1) >> shift;
        var maxCellX = (worldX0 + Chunk.Size) >> shift;
        var minCellZ = (worldZ0 - 1) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size) >> shift;
        Span<int> dx = stackalloc int[4];
        Span<int> dz = stackalloc int[4];
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetBoulder(cellX, cellZ, out var centerX, out var centerZ, out var count, out var blockId))
                continue;
            var biome = TerrainGenerator.BiomeAt(centerX, centerZ);
            if (biome != Biome.Forest && biome != Biome.Taiga) continue;

            var heights = ColumnCache.GetHeights(VoxelCoords.WorldToChunk(centerX), VoxelCoords.WorldToChunk(centerZ));
            var height = heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(centerX),
                VoxelCoords.WorldToLocal(centerZ))];
            if (TerrainGenerator.IsWaterColumn(centerX, centerZ, height)) continue;

            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;

            // The blob's cells: the center first, then its neighbors in a fixed order, one row of
            // the shape hash per count so a small boulder is a subset of a larger one's cells
            var shape = SplitMix64.Mix(SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x80D1) + ShapeStream);
            dx[0] = 0;
            dz[0] = 0;
            dx[1] = 1;
            dz[1] = 0;
            dx[2] = 0;
            dz[2] = 1;
            dx[3] = -1;
            dz[3] = 0;
            for (var i = 0; i < count && i < 4; i++)
            {
                // The neighbors' order is turned per boulder, so the blob is not always the same L
                var turn = (int)(shape >> (i * 8) & 3);
                var (ox, oz) = ((turn + 0) & 3) switch
                {
                    0 => (dx[i], dz[i]),
                    1 => (-dz[i], dx[i]),
                    2 => (-dx[i], -dz[i]),
                    _ => (dz[i], -dx[i]),
                };
                var lx = centerX + ox - worldX0;
                var lz = centerZ + oz - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
                var index = Chunk.Idx(lx, ly, lz);
                if (blocks[index] != BlockType.Air) continue;
                blocks[index] = blockId;
            }
        }
    }

    private static float Unit(ulong h) => (h >> 40) * (1f / (1 << 24));
}
