using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Chunk/world coordinate math shared by generation, physics and scheduling code.
/// Chunk storage packs index = (x &lt;&lt; 8) | (y &lt;&lt; 4) | z and column index =
/// (x &lt;&lt; 4) | z; world-to-chunk uses floor division so negative worlds map correctly.
/// </summary>
public static class VoxelCoords
{
    /// <summary>Chunk index holding a world block coordinate (floor-divides by <see cref="Chunk.Size"/>).</summary>
    public static int WorldToChunk(int world) => MathUtils.FloorDiv(world, Chunk.Size);

    /// <summary>Local cell inside its chunk for a world block coordinate (always 0..15).</summary>
    public static int WorldToLocal(int world) => MathUtils.Mod(world, Chunk.Size);

    /// <summary>Packed block index for chunk-local (x, y, z), each 0..15.</summary>
    public static int LocalIndex(int x, int y, int z) => (x << 8) | (y << 4) | z;

    /// <summary>Packed column index for chunk-local (x, z), each 0..15.</summary>
    public static int ColumnIndex(int x, int z) => (x << Chunk.SizeShift) | z;

    /// <summary>Block cell a world position sits in, floored per axis.</summary>
    public static (int X, int Y, int Z) BlockCellOf(Vector3 worldPos) => worldPos.BlockCell();

    /// <summary>Chunk position (in chunk units) holding a world position.</summary>
    public static Vector3 ChunkPosOf(Vector3 worldPos)
    {
        var (x, y, z) = worldPos.BlockCell();
        return new Vector3(WorldToChunk(x), WorldToChunk(y), WorldToChunk(z));
    }

    /// <summary>Chunk position (in chunk units) holding a world block cell.</summary>
    public static Vector3 ChunkPosOf(int blockX, int blockY, int blockZ) =>
        new(WorldToChunk(blockX), WorldToChunk(blockY), WorldToChunk(blockZ));

    /// <summary>Local cell inside its chunk for a world block cell (each 0..15).</summary>
    public static (int X, int Y, int Z) LocalOf(int blockX, int blockY, int blockZ) =>
        (WorldToLocal(blockX), WorldToLocal(blockY), WorldToLocal(blockZ));

    /// <summary>
    /// Inclusive block-cell range an AABB (center +/- halfExtent) touches per axis.
    /// Replaces the six-line min/max Floor block repeated by every physics body.
    /// </summary>
    public static void CellRange(Vector3 center, float halfExtent, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ) =>
        CellRange(center - new Vector3(halfExtent), center + new Vector3(halfExtent), out minX, out minY, out minZ, out maxX, out maxY, out maxZ);

    /// <summary>Inclusive block-cell range a world-space [min, max] box touches per axis.</summary>
    public static void CellRange(Vector3 min, Vector3 max, out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
    {
        minX = MathUtils.FloorToInt(min.X);
        minY = MathUtils.FloorToInt(min.Y);
        minZ = MathUtils.FloorToInt(min.Z);
        maxX = MathUtils.FloorToInt(max.X);
        maxY = MathUtils.FloorToInt(max.Y);
        maxZ = MathUtils.FloorToInt(max.Z);
    }
}
