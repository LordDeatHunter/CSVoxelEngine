using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Structures;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// Writes a <see cref="StructureTemplate"/> into the chunk it crosses, clipped at the chunk's border and to a world-y window,
/// the way a tree crosses chunk borders: every chunk the footprint reaches calls it with the same anchor and writes its own
/// part, so the seams agree with no cross-chunk writes. GL-free and pure of its arguments.
/// </summary>
public static class StructureStamper
{
    /// <summary>The footprint's west-north corner column for a template centered on column <paramref name="center"/>.</summary>
    public static int OriginOf(int center, StructureTemplate template) => center - template.Size / 2;

    /// <summary>
    /// Writes the cells of <paramref name="template"/>, turned <paramref name="turns"/> quarter turns clockwise, whose west-north
    /// bottom corner sits at (<paramref name="originX"/>, <paramref name="originY"/>, <paramref name="originZ"/>), into the chunk
    /// whose minimum corner is (<paramref name="worldX0"/>, <paramref name="worldY0"/>, <paramref name="worldZ0"/>). A cell
    /// outside <paramref name="minY"/>..<paramref name="maxY"/> is skipped. A chest cell is left empty when
    /// <paramref name="keepChests"/> is false. Returns whether anything was written.
    /// </summary>
    public static bool Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0,
        StructureTemplate template, int turns, int originX, int originY, int originZ, int minY, int maxY, bool keepChests = true)
    {
        var size = template.Size;
        var xLo = Math.Max(0, worldX0 - originX);
        var xHi = Math.Min(size - 1, worldX0 + Chunk.Size - 1 - originX);
        var zLo = Math.Max(0, worldZ0 - originZ);
        var zHi = Math.Min(size - 1, worldZ0 + Chunk.Size - 1 - originZ);
        // Long math: the window bounds are often int.MinValue / int.MaxValue
        var yLo = (int)Math.Max(Math.Max(0L, worldY0 - (long)originY), minY - (long)originY);
        var yHi = (int)Math.Min(Math.Min(template.Height - 1L, worldY0 + Chunk.Size - 1L - originY), maxY - (long)originY);

        var wrote = false;
        for (var y = yLo; y <= yHi; y++)
        for (var z = zLo; z <= zHi; z++)
        for (var x = xLo; x <= xHi; x++)
        {
            var cell = template.CellAt(turns, x, y, z);
            if (cell < 0) continue;

            var index = Chunk.Idx(originX + x - worldX0, originY + y - worldY0, originZ + z - worldZ0);
            var id = (byte)(cell & 0xFF);
            if (!keepChests && id != BlockType.Air && BlockType.ById[id].Chest != null)
            {
                blocks[index] = 0;
                if (state != null) state[index] = 0;
                wrote = true;
                continue;
            }
            blocks[index] = id;

            // Assign, never OR: the block id decides what the state byte means
            var cellState = (byte)(cell >> 8);
            if (cellState != 0 || state != null)
                (state ??= new byte[Chunk.Size * Chunk.Size * Chunk.Size])[index] = cellState;
            wrote = true;
        }
        return wrote;
    }
}
