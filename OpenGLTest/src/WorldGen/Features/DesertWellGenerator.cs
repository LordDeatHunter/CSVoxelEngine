using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// The desert well: a rare hand-stamped ruin — a sandstone wall ringing a sunken pool — out in the
/// dunes. Decided per 2^<see cref="WorldFeatureConfig.WellCellShift"/>-block cell from a pure
/// hash of (cell, seed), so every chunk the 5x5 footprint crosses stamps the same cells (clipped at
/// its border, the way trees cross borders); no template system. Only a flat, dry desert site takes
/// one, and the far LOD shows none.
/// </summary>
public static class DesertWellGenerator
{
    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort SandstoneBlock;
    public static readonly ushort WaterBlock;

    /// <summary>The footprint's half width: the 5x5 well reaches this many columns from its center.</summary>
    internal const int FootprintRadius = 2;

    static DesertWellGenerator()
    {
        SandstoneBlock = ContentRegistry.RequireBlock("sandstone").Id;
        WaterBlock = ContentRegistry.RequireBlock("water").Id;
    }

    /// <summary>
    /// The well a cell holds: the center column of its 5x5 footprint (jittered inside the cell), or
    /// false for the many cells without one. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetWell(int cellX, int cellZ, out int x, out int z)
    {
        x = 0;
        z = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x3117);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldFeatureConfig.WellChance) return false;

        var span = (ulong)(1 << WorldFeatureConfig.WellCellShift);
        x = (cellX << WorldFeatureConfig.WellCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldFeatureConfig.WellCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        return true;
    }

    /// <summary>
    /// Stamps every well whose footprint reaches this chunk: a 5x5 sandstone wall one cell above
    /// the ground, a sandstone floor and one water source in the middle, into air and the surface
    /// cell only, clipped at the chunk's border. Runs on a generation worker thread after the
    /// trees.
    /// </summary>
    public static void Stamp(ushort[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var shift = WorldFeatureConfig.WellCellShift;
        var minCellX = (worldX0 - FootprintRadius) >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1 + FootprintRadius) >> shift;
        var minCellZ = (worldZ0 - FootprintRadius) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1 + FootprintRadius) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetWell(cellX, cellZ, out var x, out var z)) continue;
            if (TerrainGenerator.BiomeAt(x, z) != Biome.Desert) continue;
            var ground = HeightAt(x, z);
            if (TerrainGenerator.IsWaterColumn(x, z, ground)) continue;
            if (!IsFlatSite(x, z, ground)) continue;
            StampWell(blocks, worldX0, worldY0, worldZ0, x, z, ground);
        }
    }

    /// <summary>
    /// The world-y band <see cref="Stamp"/> can write, given the tallest ground height within
    /// <see cref="FootprintRadius"/> of the chunk: <see cref="StampWell"/> writes only at
    /// <c>ground</c> (the floor and the water) and <c>ground + 1</c> (the wall), for the center
    /// column's ground, and a well that writes here has its center within
    /// <see cref="FootprintRadius"/> of the chunk. Its site may sit in a neighbor chunk column, so
    /// only the upper bound is used.
    /// </summary>
    internal static StampBand WriteBand(int neighborhoodMaxHeight) => new(int.MinValue, neighborhoodMaxHeight + 1);

    /// <summary>
    /// Writes one well centered on column (x, z) whose ground is <paramref name="ground"/>: the
    /// 5x5 wall one cell up, the inner 3x3 ring a sandstone floor at the ground's own level (its
    /// center the water source), into air and the surface cell only, clipped to the chunk whose origin
    /// is (<paramref name="worldX0"/>, <paramref name="worldY0"/>, <paramref name="worldZ0"/>). The test seam for the shape.
    /// </summary>
    internal static void StampWell(ushort[] blocks, int worldX0, int worldY0, int worldZ0, int x, int z, int ground)
    {
        for (var dx = -FootprintRadius; dx <= FootprintRadius; dx++)
        for (var dz = -FootprintRadius; dz <= FootprintRadius; dz++)
        {
            var lx = x + dx - worldX0;
            var lz = z + dz - worldZ0;
            if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
            var ring = Math.Max(Math.Abs(dx), Math.Abs(dz));

            // The wall one above the ground, the floor at the ground's own level
            var y = ground + (ring == FootprintRadius ? 1 : 0);
            var ly = y - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            var index = Chunk.Idx(lx, ly, lz);
            if (ring == FootprintRadius)
            {
                if (blocks[index] == BlockType.Air) blocks[index] = SandstoneBlock;
            }
            else if (dx == 0 && dz == 0)
            {
                blocks[index] = WaterBlock;
            }
            else
            {
                blocks[index] = SandstoneBlock;
            }
        }
    }

    // The footprint must sit exactly level: every column at the center's ground, so the
    // wall never floats over a dune or buries itself in one
    private static bool IsFlatSite(int x, int z, int ground)
    {
        for (var dx = -FootprintRadius; dx <= FootprintRadius; dx++)
        for (var dz = -FootprintRadius; dz <= FootprintRadius; dz++)
        {
            if (HeightAt(x + dx, z + dz) != ground) return false;
        }
        return true;
    }

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
            [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
}
