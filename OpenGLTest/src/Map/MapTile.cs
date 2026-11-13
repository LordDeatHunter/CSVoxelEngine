namespace VoxelEngine.Map;

/// <summary>
/// One chunk column of the map for one layer: a texel per world column, row-major with z down the rows (north at the
/// top), as packed sRGB RGBA (alpha 0 is unexplored), and the height each column's color came from (the relief
/// shading reads its northern neighbor's). GL-free.
/// </summary>
public sealed class MapTile(int chunkX, int chunkZ)
{
    public const int Size = 16;
    public const int TexelCount = Size * Size;

    /// <summary>The height of a texel that found nothing.</summary>
    public const short NoHeight = short.MinValue;

    /// <summary>The chunk column; settable so the tracker can reuse a tile whose scan found nothing.</summary>
    public int ChunkX { get; internal set; } = chunkX;
    public int ChunkZ { get; internal set; } = chunkZ;

    /// <summary>Texel (lx, lz) at index lz * <see cref="Size"/> + lx.</summary>
    public uint[] Texels { get; } = new uint[TexelCount];

    public short[] Heights { get; } = new short[TexelCount];

    /// <summary>Whether any texel holds a color: a tile scanned before any of its chunks loaded holds none.</summary>
    public bool AnyKnown
    {
        get
        {
            foreach (var texel in Texels)
                if (texel >> 24 != 0) return true;
            return false;
        }
    }

    public static int IndexOf(int localX, int localZ) => localZ * Size + localX;
}
