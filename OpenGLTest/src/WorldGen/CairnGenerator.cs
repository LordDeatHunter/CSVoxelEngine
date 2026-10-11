using System;

namespace silkgltriangle;

/// <summary>
/// Cairns: a small cobblestone stack — three cells and a one-stone pebble hat — on a local height
/// maximum above <see cref="WorldGenerationConfig.MountainRockHeight"/>, as a landmark on the
/// peaks. Decided per 2^<see cref="WorldGenerationConfig.CairnCellShift"/>-block cell from a pure
/// hash of (cell, seed); the centre column's height and its eight neighbours' come from
/// <see cref="ColumnCache"/> like the pebble scatter's, so a cairn near a chunk border is whole.
/// The far LOD shows none.
/// </summary>
public static class CairnGenerator
{
    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte CobblestoneBlock;

    /// <summary>Hat block (one stone of a pebble pile); resolved once in the static constructor.</summary>
    public static readonly byte PebbleBlock;

    static CairnGenerator()
    {
        CobblestoneBlock = ContentRegistry.RequireBlock("cobblestone").Id;
        PebbleBlock = ContentRegistry.RequireBlock("pebble").Id;
    }

    /// <summary>
    /// The cairn a cell holds: the summit column it stands on, jittered inside the cell, or false
    /// for the many cells without one. A pure function of the cell and the seed.
    /// </summary>
    public static bool TryGetCairn(int cellX, int cellZ, out int x, out int z)
    {
        x = 0;
        z = 0;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0xCA12);
        if ((SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) >= WorldGenerationConfig.CairnChance) return false;

        var span = (ulong)(1 << WorldGenerationConfig.CairnCellShift);
        x = (cellX << WorldGenerationConfig.CairnCellShift) + (int)(SplitMix64.Mix(h + 1) % span);
        z = (cellZ << WorldGenerationConfig.CairnCellShift) + (int)(SplitMix64.Mix(h + 2) % span);
        return true;
    }

    /// <summary>
    /// Whether the column (x, z) is a summit a cairn may stand on: above
    /// <see cref="WorldGenerationConfig.MountainRockHeight"/> and at least as high as its eight
    /// neighbours.
    /// </summary>
    public static bool IsSummit(int x, int z)
    {
        var height = HeightAt(x, z);
        if (height < WorldGenerationConfig.MountainRockHeight) return false;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            if (HeightAt(x + dx, z + dz) > height) return false;
        }
        return true;
    }

    /// <summary>
    /// Stamps every cairn that reaches this chunk: its stack into air only, clipped at the chunk's
    /// top and bottom. Runs on a generation worker thread after the trees.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var shift = WorldGenerationConfig.CairnCellShift;
        var minCellX = worldX0 >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1) >> shift;
        var minCellZ = worldZ0 >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetCairn(cellX, cellZ, out var x, out var z)) continue;
            if (x < worldX0 || x >= worldX0 + Chunk.Size || z < worldZ0 || z >= worldZ0 + Chunk.Size) continue;
            if (!IsSummit(x, z)) continue;
            StampStack(blocks, worldY0, x, z, HeightAt(x, z));
        }
    }

    /// <summary>
    /// Writes one cairn's stack at column (x, z) whose ground is <paramref name="height"/>: three
    /// cobblestone cells and a one-stone pebble hat, into air only. The test seam for the shape.
    /// </summary>
    internal static void StampStack(byte[] blocks, int worldY0, int x, int z, int height)
    {
        var lx = x - worldX0Of(x);
        var lz = z - worldX0Of(z);
        for (var i = 1; i <= 3; i++)
        {
            var ly = height + i - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            var index = Chunk.Idx(lx, ly, lz);
            if (blocks[index] != BlockType.Air) continue;
            blocks[index] = CobblestoneBlock;
        }

        var hatLy = height + 4 - worldY0;
        if ((uint)hatLy < Chunk.Size)
        {
            var index = Chunk.Idx(lx, hatLy, lz);
            if (blocks[index] == BlockType.Air)
                blocks[index] = PebbleBlock;
        }
    }

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z))
            [VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];

    // The chunk origin a world column falls in (StampStack is handed the worldY0 alone)
    private static int worldX0Of(int x) => x & ~Chunk.SizeMask;
}
