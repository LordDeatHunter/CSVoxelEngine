using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Player;

/// <summary>
/// Reads the state of a player's body from the live chunk map alone, with no simulation state:
/// whether it stands on ground, swims, touches lava, and whether the cells that decide those
/// are loaded at all. The dedicated server judges every connected player through it (it never
/// simulates their bodies, only the positions their states report), and the local physics shares
/// its lava rule (<see cref="PlayerPhysics.UpdateWaterState"/>). GL-free; main thread only, like
/// every read of the live chunk map.
/// </summary>
public static class PlayerBodyProbe
{
    /// <summary>
    /// Whether the body's surroundings are loaded: the chunk of the feet cell and of the cell
    /// <see cref="WorldGenerationConfig.NetGroundProbeBlocks"/> below the feet. When they are not,
    /// nothing about the body can be judged (an unloaded cell would read as air), so callers treat
    /// the body as unknown and judge nothing.
    /// </summary>
    public static bool IsKnown(Vector3 eye, ChunkManager chunks)
    {
        var feet = eye.Y - Camera.EyeHeight;
        var x = MathUtils.FloorToInt(eye.X);
        var z = MathUtils.FloorToInt(eye.Z);
        return chunks.ChunkAt(x, MathUtils.FloorToInt(feet), z) != null
               && chunks.ChunkAt(x, MathUtils.FloorToInt(feet - WorldGenerationConfig.NetGroundProbeBlocks), z) != null;
    }

    /// <summary>
    /// Whether the body stands on something: the body box lowered by
    /// <see cref="WorldGenerationConfig.NetGroundProbeBlocks"/> collides under the player-body rule
    /// (<see cref="PlayerPhysics.BodyCollides"/>). A body already inside a solid reads grounded
    /// too, so a buried player is never billed a fall.
    /// </summary>
    public static bool Grounded(Vector3 eye, ChunkManager chunks) =>
        PlayerPhysics.BodyCollides(eye - new Vector3(0f, WorldGenerationConfig.NetGroundProbeBlocks, 0f), chunks);

    /// <summary>
    /// Whether the body swims: a fluid surface over its footprint reaches above the feet, within
    /// <see cref="PlayerPhysics.SwimDepth"/> of them. The same test as the swim half of
    /// <see cref="PlayerPhysics.UpdateWaterState"/>; keep the two together.
    /// </summary>
    public static bool InWater(Vector3 eye, ChunkManager chunks)
    {
        var feetY = eye.Y - Camera.EyeHeight;
        var min = new Vector3(eye.X - PlayerPhysics.PlayerRadius, feetY, eye.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(eye.X + PlayerPhysics.PlayerRadius, feetY + PlayerPhysics.SwimDepth,
            eye.Z + PlayerPhysics.PlayerRadius);
        VoxelCoords.CellRange(min, max, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (chunks.GetFluidSurfaceGlobal(x, y, z) > feetY)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether any cell of the body box holds a burning fluid (a fluid with a light emission:
    /// lava). Lava burns on body contact, not just eye contact, so a wade through a shallow pool
    /// keeps the eye dry but still hurts. An unloaded cell is not lava.
    /// </summary>
    public static bool InLava(Vector3 eye, ChunkManager chunks)
    {
        var feetY = eye.Y - Camera.EyeHeight;
        var min = new Vector3(eye.X - PlayerPhysics.PlayerRadius, feetY, eye.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(eye.X + PlayerPhysics.PlayerRadius, feetY + PlayerPhysics.PlayerHeight,
            eye.Z + PlayerPhysics.PlayerRadius);
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
