using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Fallen logs: a rare detached oak log lying along X or Z on the forest floor, decided per
/// 2^<see cref="WorldGenerationConfig.FallenLogCellShift"/>-block cell from a pure hash of
/// (cell, seed), so every chunk the log reaches stamps the same cells (clipped at its border).
/// <see cref="WoodState.Detached"/> cubes never weld, fell or feed leaves, so a log is scenery,
/// not a tree; the far LOD shows none. The start column's ground is read from
/// <see cref="ColumnCache"/> like the pebble scatter's, so a log near a chunk border is whole.
/// </summary>
public static class FallenLogGenerator
{
    private const ulong AcceptStream = 0x9E3779B97F4A7C15UL;
    private const ulong StartXStream = unchecked(AcceptStream * 2);
    private const ulong StartZStream = unchecked(AcceptStream * 3);
    private const ulong LengthStream = unchecked(AcceptStream * 4);
    private const ulong AxisStream = unchecked(AcceptStream * 5);

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte LogBlock;

    static FallenLogGenerator() => LogBlock = ContentRegistry.RequireBlock("oak_log").Id;

    /// <summary>
    /// The log a cell holds: its start column (jittered inside the cell), its length and its axis
    /// (along X or along Z), or false for the many cells without one. A pure function of the cell
    /// and the seed.
    /// </summary>
    public static bool TryGetLog(int cellX, int cellZ, out int startX, out int startZ, out int length,
        out bool alongX)
    {
        startX = startZ = 0;
        length = 0;
        alongX = true;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x10A5);
        if (Unit(SplitMix64.Mix(h + AcceptStream)) >= WorldGenerationConfig.FallenLogChance) return false;

        var size = 1 << WorldGenerationConfig.FallenLogCellShift;
        var span = (ulong)(size - WorldGenerationConfig.FallenLogMaxLength + 1);
        startX = (cellX << WorldGenerationConfig.FallenLogCellShift) + (int)(SplitMix64.Mix(h + StartXStream) % span);
        startZ = (cellZ << WorldGenerationConfig.FallenLogCellShift) + (int)(SplitMix64.Mix(h + StartZStream) % span);
        var lengths = WorldGenerationConfig.FallenLogMaxLength - WorldGenerationConfig.FallenLogMinLength + 1;
        length = WorldGenerationConfig.FallenLogMinLength + (int)(SplitMix64.Mix(h + LengthStream) % (ulong)lengths);
        alongX = (SplitMix64.Mix(h + AxisStream) & 1) == 0;
        return true;
    }

    /// <summary>
    /// Stamps every log that reaches this chunk: its cells at one height (the start column's
    /// ground plus one), into air only, clipped at the chunk's border. Runs on a generation worker
    /// thread after the trees, so a log never displaces one.
    /// </summary>
    public static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var reach = WorldGenerationConfig.FallenLogMaxLength;
        var shift = WorldGenerationConfig.FallenLogCellShift;
        var minCellX = (worldX0 - reach) >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1) >> shift;
        var minCellZ = (worldZ0 - reach) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetLog(cellX, cellZ, out var startX, out var startZ, out var length, out var alongX)) continue;
            if (TerrainGenerator.BiomeAt(startX, startZ) != Biome.Forest) continue;

            var heights = ColumnCache.GetHeights(VoxelCoords.WorldToChunk(startX), VoxelCoords.WorldToChunk(startZ));
            var height = heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(startX),
                VoxelCoords.WorldToLocal(startZ))];
            if (TerrainGenerator.IsWaterColumn(startX, startZ, height)) continue;

            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            for (var i = 0; i < length; i++)
            {
                var lx = (alongX ? startX + i : startX) - worldX0;
                var lz = (alongX ? startZ : startZ + i) - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
                var index = Chunk.Idx(lx, ly, lz);
                if (blocks[index] != BlockType.Air) continue;
                blocks[index] = LogBlock;
                (state ??= new byte[Chunk.Size * Chunk.Size * Chunk.Size])[index] = WoodState.Detached;
            }
        }
    }

    private static float Unit(ulong h) => (h >> 40) * (1f / (1 << 24));
}
