using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Cave formations: stone points hanging from cave ceilings and rising from cave floors, decided
/// per cell from a pure hash of (cell, seed) so chunks agree at borders. A ceiling cell (air under
/// solid rock) takes a stalactite, a floor cell (air over solid rock) a stalagmite, both at least
/// <see cref="WorldGenerationConfig.CaveFormationMinDepth"/> under the column's height; a
/// stalactite also drips a stalagmite onto the floor of its own column when that cell is free, so
/// the two read as a pair. Both blocks carry needsSupport (above and below), so mining the rock
/// they hang from or stand on pops them; the far LOD shows none.
/// </summary>
public static class CaveFormationGenerator
{
    private const ulong StalactiteStream = 0x9E3779B97F4A7C15UL;
    private const ulong StalagmiteStream = unchecked(StalactiteStream * 2);

    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte StalactiteBlock;
    public static readonly byte StalagmiteBlock;

    static CaveFormationGenerator()
    {
        StalactiteBlock = ContentRegistry.RequireBlock("stalactite").Id;
        StalagmiteBlock = ContentRegistry.RequireBlock("stalagmite").Id;
    }

    /// <summary>Whether a ceiling cell (x, y, z) carries a stalactite, before the terrain checks.</summary>
    public static bool HasStalactite(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x, y ^ z, WorldGenerationConfig.Seed ^ 0x5A1A);
        return (SplitMix64.Mix(h + StalactiteStream) >> 40) * (1f / (1 << 24))
               < WorldGenerationConfig.StalactiteChance;
    }

    /// <summary>Whether a floor cell (x, y, z) carries a stalagmite, before the terrain checks.</summary>
    public static bool HasStalagmite(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x, y ^ z, WorldGenerationConfig.Seed ^ 0x5A1A);
        return (SplitMix64.Mix(h + StalagmiteStream) >> 40) * (1f / (1 << 24))
               < WorldGenerationConfig.StalagmiteChance;
    }

    /// <summary>
    /// Stamps every formation this chunk holds: a stalactite on a hashed ceiling cell, a
    /// stalagmite on a hashed floor cell, and a stalagmite under a stalactite's own drip point
    /// (the first solid cell below it in its column, its own cell free). Runs on a generation
    /// worker thread after the carvers and the water, so a flooded cave stays bare.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            var top = Math.Min(height - WorldGenerationConfig.CaveFormationMinDepth,
                worldY0 + Chunk.Size - 2);
            for (var y = top; y >= worldY0; y--)
            {
                var ly = y - worldY0;
                if (ly <= 0) break;
                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != BlockType.Air) continue;

                var ceiling = blocks[i + Chunk.Size] != BlockType.Air;
                var floor = blocks[i - Chunk.Size] != BlockType.Air;
                if (ceiling && HasStalactite(worldX0 + x, y, worldZ0 + z))
                {
                    blocks[i] = StalactiteBlock;
                    // Its drip point: the first free cell over the column's floor below it
                    for (var drip = y - 1; drip > worldY0; drip--)
                    {
                        var dl = drip - worldY0;
                        if (dl <= 0) break;
                        var di = Chunk.Idx(x, dl, z);
                        if (blocks[di] != BlockType.Air) break;
                        if (blocks[di - Chunk.Size] == BlockType.Air) continue;
                        blocks[di] = StalagmiteBlock;
                        break;
                    }
                }
                else if (floor && HasStalagmite(worldX0 + x, y, worldZ0 + z))
                {
                    blocks[i] = StalagmiteBlock;
                }
            }
        }
    }
}
