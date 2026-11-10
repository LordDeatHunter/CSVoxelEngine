using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// One solid cloud layer's shape, shared by the cloud patches (<see cref="CloudPatchGenerator"/>) and the
/// cloud sea (<see cref="CloudSeaGenerator"/>): a column holds cloud where a coherent 2D cover noise is
/// above <see cref="Threshold"/>; how far above sets its thickness, one cell at the edge up to the whole
/// band from <see cref="CoreValue"/> on, roughened by a finer noise that also lifts the top, so the cloud is
/// a flat-bottomed, unevenly domed slab. A pure function of (x, z) and the noises' seed:
/// <see cref="TryGetColumn"/> is the one rule the stamp and the point query share. Immutable, so it is
/// safe on every generation worker; a reseed builds a new one.
/// </summary>
internal sealed class CloudLayer(
    int bottomY, int topY, FastNoiseLite cover, FastNoiseLite detail, float threshold, float coreValue)
{
    /// <summary>Lowest cell of the band.</summary>
    internal int BottomY { get; } = bottomY;

    /// <summary>Highest cell of the band.</summary>
    internal int TopY { get; } = topY;

    /// <summary>The cover value above which a column holds cloud.</summary>
    internal float Threshold { get; } = threshold;

    /// <summary>The cover value from which a column has the band's full thickness.</summary>
    internal float CoreValue { get; } = coreValue;

    /// <summary>How many cells the band spans.</summary>
    internal int Height => TopY - BottomY + 1;

    /// <summary>
    /// The cloud cells of column (x, z), inclusive world y: false where the column holds none. The range
    /// always lies inside the band, at least one cell thick, thicker the deeper the column lies in the cover.
    /// </summary>
    internal bool TryGetColumn(int x, int z, out int bottom, out int top)
    {
        bottom = 0;
        top = -1;
        var value = cover.GetNoise(x, z);
        if (value <= Threshold) return false;

        var strength = MathF.Min(1f, (value - Threshold) / (CoreValue - Threshold));
        var lift = 0.5f * (detail.GetNoise(x, z) + 1f);
        var height = Height;
        var thickness = Math.Clamp(1 + (int)(strength * (height - 1) * (0.6f + 0.4f * lift) + 0.5f), 1, height);

        // The spare cells mostly go above the cloud: a flat-ish bottom under a lifted, uneven top
        var room = height - thickness;
        bottom = BottomY + (int)(room * (0.2f + 0.3f * lift) + 0.5f);
        top = bottom + thickness - 1;
        return true;
    }

    /// <summary>Whether the cell belongs to the layer; on air this is what <see cref="Stamp"/> writes.</summary>
    internal bool Contains(int x, int y, int z) =>
        TryGetColumn(x, z, out var bottom, out var top) && y >= bottom && y <= top;

    /// <summary>Writes <paramref name="block"/> into the air cells of the layer in the chunk whose minimum
    /// corner is (worldX0, worldY0, worldZ0).</summary>
    internal void Stamp(ushort[] blocks, ushort block, int worldX0, int worldY0, int worldZ0)
    {
        var yMin = Math.Max(0, BottomY - worldY0);
        var yMax = Math.Min(Chunk.Size - 1, TopY - worldY0);
        if (yMin > yMax) return;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (!TryGetColumn(worldX0 + x, worldZ0 + z, out var bottom, out var top)) continue;

            var lo = Math.Max(yMin, bottom - worldY0);
            var hi = Math.Min(yMax, top - worldY0);
            for (var y = lo; y <= hi; y++)
            {
                var i = Chunk.Idx(x, y, z);
                if (blocks[i] == BlockType.Air)
                    blocks[i] = block;
            }
        }
    }
}
