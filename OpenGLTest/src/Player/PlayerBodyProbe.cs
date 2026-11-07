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
    /// <see cref="NetworkConfig.NetGroundProbeBlocks"/> below the feet. When they are not,
    /// nothing about the body can be judged (an unloaded cell would read as air), so callers treat
    /// the body as unknown and judge nothing.
    /// </summary>
    public static bool IsKnown(Vector3 eye, ChunkManager chunks)
    {
        var feet = eye.Y - Camera.EyeHeight;
        var x = MathUtils.FloorToInt(eye.X);
        var z = MathUtils.FloorToInt(eye.Z);
        return chunks.ChunkAt(x, MathUtils.FloorToInt(feet), z) != null
               && chunks.ChunkAt(x, MathUtils.FloorToInt(feet - NetworkConfig.NetGroundProbeBlocks), z) != null;
    }

    /// <summary>
    /// Whether the body stands on something: the body box lowered by
    /// <see cref="NetworkConfig.NetGroundProbeBlocks"/> collides under the player-body rule
    /// (<see cref="BodyCollision.BodyCollides"/>), or its feet stand in a sink block's soft layer
    /// (<see cref="SinkLayerTop"/>), which the client's physics treats as ground while the body
    /// sinks through it. A body already inside a solid reads grounded too, so a buried player is
    /// never billed a fall. <paramref name="solids"/> adds the non-block solids (the server's
    /// icebergs), so a rider stands on its berg.
    /// </summary>
    public static bool Grounded(Vector3 eye, ChunkManager chunks, IBodySolids? solids = null) =>
        BodyCollision.BodyCollides(eye - new Vector3(0f, NetworkConfig.NetGroundProbeBlocks, 0f), chunks, solids)
        || SinkLayerTop(eye, chunks) != null;

    /// <summary>
    /// The top of the sink block's soft layer the feet stand in (<see cref="SinkLayer.FeetIn"/>), read
    /// over the body's footprint in the cell just under the feet, or null when they stand in none.
    /// An unloaded cell reads as air, so it holds no layer.
    /// </summary>
    public static float? SinkLayerTop(Vector3 eye, ChunkManager chunks)
    {
        var feet = eye.Y - Camera.EyeHeight;
        var y = MathUtils.FloorToInt(feet - SinkLayer.FeetTolerance);
        FootprintCells(eye, out var minX, out var minZ, out var maxX, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (SinkLayer.FeetIn(feet, y, BlockType.SinkOf(chunks.GetBlockGlobal(x, y, z))))
                return y + 1f;
        }
        return null;
    }

    /// <summary>
    /// Whether a landing here is cushioned and bills no fall damage: a bounce block (a slime block)
    /// or a soft-landing block (a cloud) lies under any part of the body's footprint, in the cell
    /// just under the feet (which, for a body sunk into a soft layer, is that block's own cell).
    /// The fall tracker reads it in singleplayer and on the server alike.
    /// </summary>
    public static bool LandingCushioned(Vector3 eye, ChunkManager chunks)
    {
        var y = MathUtils.FloorToInt(eye.Y - Camera.EyeHeight - SinkLayer.FeetTolerance);
        FootprintCells(eye, out var minX, out var minZ, out var maxX, out var maxZ);
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            var id = chunks.GetBlockGlobal(x, y, z);
            if (BlockType.BouncesAt(id) || BlockType.SoftLandingAt(id))
                return true;
        }
        return false;
    }

    // The block columns the body's footprint covers
    private static void FootprintCells(Vector3 eye, out int minX, out int minZ, out int maxX, out int maxZ)
    {
        minX = MathUtils.FloorToInt(eye.X - PlayerPhysics.PlayerRadius);
        maxX = MathUtils.FloorToInt(eye.X + PlayerPhysics.PlayerRadius);
        minZ = MathUtils.FloorToInt(eye.Z - PlayerPhysics.PlayerRadius);
        maxZ = MathUtils.FloorToInt(eye.Z + PlayerPhysics.PlayerRadius);
    }

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
    /// Whether the eye is under a fluid surface (the eye cell's own fluid height, so a one-block
    /// pool's 8/9 surface leaves a standing eye dry), and whether that fluid burns
    /// (<paramref name="inLava"/>). The one eye rule <see cref="PlayerPhysics.UpdateWaterState"/>,
    /// the shoulder camera's eye and the server's drowning all read.
    /// </summary>
    public static bool EyeInFluid(Vector3 eye, ChunkManager chunks, out bool inLava)
    {
        inLava = false;
        var (x, y, z) = eye.BlockCell();
        if (eye.Y >= chunks.GetFluidSurfaceGlobal(x, y, z))
            return false;
        inLava = chunks.TryGetCell(x, y, z, out var id, out _) && BlockType.IsFluid(id) && BlockType.Emission[id] > 0;
        return true;
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
        return BurningFluid.AnyInBox(min, max, chunks);
    }
}
