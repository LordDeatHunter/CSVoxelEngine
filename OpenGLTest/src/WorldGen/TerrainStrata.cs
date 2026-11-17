using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.WorldGen;

/// <summary>Seeded horizontal rock bands on exposed Dynamic arid walls. The selector reads only
/// position, biome and the current quiescent world seed; column and feature ownership stay with
/// the fill and LOD callers.</summary>
internal static class TerrainStrata
{
    private static readonly ushort RedSandstone = ContentRegistry.RequireBlock("red_sandstone").Id;
    private static readonly ushort OchreClay = ContentRegistry.RequireBlock("ochre_clay").Id;

    internal static bool IsArid(Biome biome) => biome is Biome.Desert or Biome.Steppe or Biome.Savanna;

    internal static ushort BlockAt(int x, int y, int z, Biome biome)
    {
        if (WorldSeed.Profile != WorldGenProfile.Dynamic || !IsArid(biome))
            return TerrainGenerator.RockBlock;

        var shifted = y + TerrainNoise.StrataNoise.GetNoise(x, z) * WorldGenerationConfig.StrataWobbleBlocks;
        var band = (int)MathF.Floor(shifted / WorldGenerationConfig.StrataBandBlocks);
        return (band & 3) switch
        {
            0 => RedSandstone,
            1 => TerrainGenerator.SandstoneBlock,
            2 => OchreClay,
            _ => TerrainGenerator.RockBlock,
        };
    }

    internal static bool Admits(ColumnData column, int index, int y) =>
        column.DirtDepth[index] == 0
        && IsArid(column.Biomes[index])
        && y <= column.Heights[index]
        && column.Heights[index] - y <= WorldGenerationConfig.StrataMaxDepth;

    internal static void Stamp(ushort[] blocks, ColumnData column, CaveBiomeMap? caves,
        int worldX0, int lowerBoundY, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var columnIndex = (x << Chunk.SizeShift) | z;
            if (column.DirtDepth[columnIndex] != 0 || !IsArid(column.Biomes[columnIndex])) continue;
            var firstY = Math.Max(lowerBoundY, column.Heights[columnIndex] - WorldGenerationConfig.StrataMaxDepth);
            var lastY = Math.Min(lowerBoundY + Chunk.Size - 1, column.Heights[columnIndex]);
            for (var y = firstY; y <= lastY; y++)
            {
                var index = Chunk.Idx(x, y & Chunk.SizeMask, z);
                if (blocks[index] != TerrainGenerator.RockBlock
                    || caves is not null && caves.Cells[index] != (byte)CaveBiome.Plain) continue;
                blocks[index] = BlockAt(worldX0 + x, y, worldZ0 + z, column.Biomes[columnIndex]);
            }
        }
    }
}
