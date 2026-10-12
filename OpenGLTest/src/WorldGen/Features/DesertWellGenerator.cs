using System;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// The desert well: a rare hand-stamped ruin — a sandstone wall ringing a sunken pool — out in the
/// dunes. Decided per 2^<see cref="WorldGenerationConfig.WellCellShift"/>-block cell from a pure
/// hash of (cell, seed), so every chunk the 5x5 footprint crosses stamps the same cells (clipped at
/// its border, the way trees cross borders); no template system. Only a flat, dry desert site takes
/// one, and the far LOD shows none.
/// </summary>
public static class DesertWellGenerator
{
    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte SandstoneBlock;
    public static readonly byte WaterBlock;

    static DesertWellGenerator()
    {
        SandstoneBlock = ContentRegistry.RequireBlock("sandstone").Id;
        WaterBlock = ContentRegistry.RequireBlock("water").Id;
    }

    /// <summary>
    /// The well a cell holds: the centre column of its 5x5 footprint (jittered inside the cell), or
    /// false for the many cells without one. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetWell(int cellX, int cellZ, out int x, out int z)
    {
        x = 0;
        z = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x3117);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.WellChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.WellCellShift);
        x = (cellX << WorldGenerationConfig.WellCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.WellCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        return true;
    }

    /// <summary>
    /// Stamps every well whose footprint reaches this chunk: a 5x5 sandstone wall one cell above
    /// the ground, a sandstone floor and one water source in the middle, into air and the surface
    /// cell only, clipped at the chunk's border. Runs on a generation worker thread after the
    /// trees.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var shift = WorldGenerationConfig.WellCellShift;
        var minCellX = (worldX0 - 2) >> shift;
        var maxCellX = (worldX0 + Chunk.Size + 1) >> shift;
        var minCellZ = (worldZ0 - 2) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size + 1) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetWell(cellX, cellZ, out var x, out var z)) continue;
            if (TerrainGenerator.BiomeAt(x, z) != Biome.Desert) continue;
            var ground = HeightAt(x, z);
            if (TerrainGenerator.IsWaterColumn(x, z, ground)) continue;
            if (!IsFlatSite(x, z, ground)) continue;
            StampWell(blocks, worldY0, x, z, ground);
        }
    }

    /// <summary>
    /// Writes one well centred on column (x, z) whose ground is <paramref name="ground"/>: the
    /// 5x5 wall one cell up, the inner 3x3 ring a sandstone floor at the ground's own level (its
    /// centre the water source), into air and the surface cell only. The test seam for the shape.
    /// </summary>
    internal static void StampWell(byte[] blocks, int worldY0, int x, int z, int ground)
    {
        for (var dx = -2; dx <= 2; dx++)
        for (var dz = -2; dz <= 2; dz++)
        {
            var lx = x + dx - worldX0Of(x);
            var lz = z + dz - worldX0Of(z);
            if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
            var ring = Math.Max(Math.Abs(dx), Math.Abs(dz));

            // The wall one above the ground, the floor at the ground's own level
            var y = ground + (ring == 2 ? 1 : 0);
            var ly = y - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            var index = Chunk.Idx(lx, ly, lz);
            if (ring == 2)
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

    // The footprint must sit level: every column within a cell of the centre's ground, so the
    // wall never floats over a dune or buries itself in one
    private static bool IsFlatSite(int x, int z, int ground)
    {
        for (var dx = -2; dx <= 2; dx++)
        for (var dz = -2; dz <= 2; dz++)
        {
            if (Math.Abs(HeightAt(x + dx, z + dz) - ground) > 1) return false;
        }
        return true;
    }

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
            [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];

    // The chunk origin a world column falls in (StampWell is handed the worldY0 alone)
    private static int worldX0Of(int x) => x & ~Chunk.SizeMask;
}
