using System;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// Boulders: a rare blob of stone or cobblestone cells sitting on the forest or taiga floor,
/// decided per 2^<see cref="WorldGenerationConfig.BoulderCellShift"/>-block cell from a pure hash
/// of (cell, seed), so every chunk a boulder reaches stamps the same cells (clipped at its
/// border). Its cells share one height (the centre column's ground plus one) and go into air only;
/// the far LOD shows none.
/// </summary>
public static class BoulderGenerator
{
    private const ulong AcceptStream = 0x9E3779B97F4A7C15UL;
    private const ulong CentreXStream = unchecked(AcceptStream * 2);
    private const ulong CentreZStream = unchecked(AcceptStream * 3);
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
    /// The boulder a cell holds: its centre column (jittered inside the cell), its block count
    /// (2..4) and its material, or false for the many cells without one. A pure function of the
    /// cell and the seed.
    /// </summary>
    public static bool TryGetBoulder(int cellX, int cellZ, out int centreX, out int centreZ, out int count,
        out byte blockId)
    {
        centreX = centreZ = 0;
        count = 0;
        blockId = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x80D1);
        if (Unit(SplitMix64.Mix(h + AcceptStream)) >= WorldGenerationConfig.BoulderChance) return false;

        var size = 1 << WorldGenerationConfig.BoulderCellShift;
        var span = (ulong)size;
        centreX = (cellX << WorldGenerationConfig.BoulderCellShift) + (int)(SplitMix64.Mix(h + CentreXStream) % span);
        centreZ = (cellZ << WorldGenerationConfig.BoulderCellShift) + (int)(SplitMix64.Mix(h + CentreZStream) % span);
        var counts = WorldGenerationConfig.BoulderMaxBlocks - WorldGenerationConfig.BoulderMinBlocks + 1;
        count = WorldGenerationConfig.BoulderMinBlocks + (int)(SplitMix64.Mix(h + CountStream) % (ulong)counts);
        blockId = (SplitMix64.Mix(h + MaterialStream) & 1) == 0 ? StoneBlock : CobblestoneBlock;
        return true;
    }

    /// <summary>
    /// Stamps every boulder that reaches this chunk: its cells around the centre at the centre
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
            if (!TryGetBoulder(cellX, cellZ, out var centreX, out var centreZ, out var count, out var blockId))
                continue;
            var biome = TerrainGenerator.BiomeAt(centreX, centreZ);
            if (biome != Biome.Forest && biome != Biome.Taiga) continue;

            var heights = ColumnCache.GetHeights(VoxelCoords.WorldToChunk(centreX), VoxelCoords.WorldToChunk(centreZ));
            var height = heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(centreX),
                VoxelCoords.WorldToLocal(centreZ))];
            if (TerrainGenerator.IsWaterColumn(centreX, centreZ, height)) continue;

            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;

            // The blob's cells: the centre first, then its neighbors in a fixed order, one row of
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
                var lx = centreX + ox - worldX0;
                var lz = centreZ + oz - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
                var index = Chunk.Idx(lx, ly, lz);
                if (blocks[index] != BlockType.Air) continue;
                blocks[index] = blockId;
            }
        }
    }

    private static float Unit(ulong h) => (h >> 40) * (1f / (1 << 24));
}
