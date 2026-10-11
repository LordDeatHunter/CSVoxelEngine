using System;

namespace silkgltriangle;

/// <summary>
/// Glow mushrooms: a rare dim light on deep cave floors, decided per column from a pure hash of
/// (x, z) and the seed. A cell at least <see cref="WorldGenerationConfig.GlowMushroomMinDepth"/>
/// under the column's height whose own cell and headroom are air and whose floor is solid takes
/// one — a cave floor, never the surface. The far LOD shows none.
/// </summary>
public static class GlowMushroomGenerator
{
    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte MushroomBlock;

    static GlowMushroomGenerator() => MushroomBlock = ContentRegistry.RequireBlock("glow_mushroom").Id;

    /// <summary>Whether the column (x, z) carries a mushroom, before the terrain checks.</summary>
    public static bool TryGetMushroom(int x, int z)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ 0x6105);
        return (SplitMix64.Mix(h) >> 40) * (1f / (1 << 24)) < WorldGenerationConfig.GlowMushroomChance;
    }

    /// <summary>
    /// Stamps mushrooms onto deep cave floors: air over a solid cell, with air above, at least
    /// <see cref="WorldGenerationConfig.GlowMushroomMinDepth"/> under the column's height. Runs on
    /// a generation worker thread after the carvers and the water, so a flooded cave stays bare.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            if (!TryGetMushroom(worldX0 + x, worldZ0 + z)) continue;

            // Down from the deepest cell this chunk could hold to the depth line (one short of the
            // chunk top, so the headroom read stays inside the chunk)
            var top = Math.Min(height - WorldGenerationConfig.GlowMushroomMinDepth,
                worldY0 + Chunk.Size - 2);
            for (var y = top; y >= worldY0; y--)
            {
                var ly = y - worldY0;
                if (ly <= 0) break;
                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != BlockType.Air) continue;
                if (blocks[i - Chunk.Size] == BlockType.Air) continue;
                if (blocks[i + Chunk.Size] != BlockType.Air) continue;
                blocks[i] = MushroomBlock;
                break;
            }
        }
    }
}
