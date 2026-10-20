using System.Numerics;

namespace VoxelEngine.Chunks;

/// <summary>
/// The burning-fluid cell test both lava rules share, GL-free: whether a box touches a fluid with a
/// light emission (lava). The mobs' rule (<c>MobLava.BodyInLava</c>) and the player's
/// (<c>PlayerBodyProbe.InLava</c>) each build their own body box and hand it here. An unloaded cell
/// is not lava. Main thread, like every read of the live chunk map.
/// </summary>
internal static class BurningFluid
{
    /// <summary>Whether any cell the box from <paramref name="min"/> to <paramref name="max"/> overlaps holds a burning fluid.</summary>
    internal static bool AnyInBox(Vector3 min, Vector3 max, ChunkManager chunks)
    {
        VoxelCoords.CellRange(min, max, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (chunks.TryGetCell(x, y, z, out var id, out _) && BlockType.IsFluid(id) && BlockType.Emission[id] > 0)
                return true;
        }
        return false;
    }
}
