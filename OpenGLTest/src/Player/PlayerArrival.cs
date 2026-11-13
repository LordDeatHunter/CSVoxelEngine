using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Player;

/// <summary>
/// Lands the local player after a jump across the world: the first spawn, a respawn, a <c>/tp</c> or a waystone
/// landing. Physics reads an unloaded cell as air, so a body dropped where the terrain is still streaming in falls into
/// it and is buried once it loads. Instead the camera is held over the destination every frame (velocity zeroed, fall
/// forgotten) until every cell the body could touch there is loaded and the chunk under it is meshed, and is then put
/// down by the body's own collision rule (<see cref="BodyCollision.BodyCollides"/>), so a walk-through block (leaves,
/// a thin snow pile) is never stood on and a solid one is never stood in:
/// <list type="bullet">
/// <item><see cref="BeginSurface"/> (the spawn): the column's top standing spot, found from above the heightmap down to
/// the first collision; a water surface stops the drop and the body floats there.</item>
/// <item><see cref="BeginAt"/> (respawn, command, waystone): the spot itself, lifted to the first free height when it
/// lands inside a solid.</item>
/// </list>
/// A camera moved by anything else while held (a scenario teleport) cancels the arrival. Main thread only.
/// </summary>
internal sealed class PlayerArrival(Camera camera, Action<float> resetFall)
{
    private enum Mode
    {
        None,
        Surface,
        At,
    }

    // The settle's hair of lift, so the first frame never starts inside the floor
    private const float Lift = 0.02f;
    // A spot inside a solid climbs at most this far before it is left as it is
    private const int MaxLiftBlocks = 64;
    // A surface search reads at most this far below the heightmap (a carved spawn column)
    private const int MaxSurfaceDepth = 64;
    // The fine settle onto a floor that is not a whole cell (a slab, a cloud's soft layer)
    private const float SettleStep = 1f / 16f;
    // A sideways move bigger than this between two held frames was somebody else's teleport. Only sideways: the one
    // physics step between two holds drops the body, by several blocks on a slow first frame
    private const float ForeignMoveSquared = 4f;

    private Mode _mode;
    private Vector3 _feet;
    private Vector3 _heldEye;
    private double _held;

    /// <summary>Whether a destination is still loading under the held player.</summary>
    internal bool IsArriving => _mode != Mode.None;

    /// <summary>Lands on the top standing spot of the column at (<paramref name="x"/>, <paramref name="z"/>), held at
    /// <paramref name="guessFeet"/> (the heightmap's guess) meanwhile.</summary>
    internal void BeginSurface(float x, float z, float guessFeet) => Begin(Mode.Surface, new Vector3(x, guessFeet, z));

    /// <summary>Lands at <paramref name="feet"/>, lifted out of any solid it is inside once the cells there load.</summary>
    internal void BeginAt(Vector3 feet) => Begin(Mode.At, feet);

    /// <summary>Drops the arrival in progress (a restored pose, a client's server spawn).</summary>
    internal void Cancel() => _mode = Mode.None;

    private void Begin(Mode mode, Vector3 feet)
    {
        _mode = mode;
        _feet = feet;
        _held = 0;
        Hold();
    }

    /// <summary>
    /// Once a frame, before the camera moves: holds the player over the destination, or lands it once the cells there
    /// are in. Past <see cref="StreamingConfig.WaystoneArrivalTimeoutSeconds"/> a
    /// destination whose cells are in but whose mesh is not lands anyway; one whose cells never load stays held.
    /// </summary>
    internal void Update(ChunkManager chunks, double deltaTime)
    {
        if (_mode == Mode.None)
            return;
        var moved = camera.Position - _heldEye;
        if (moved.X * moved.X + moved.Z * moved.Z > ForeignMoveSquared)
        {
            _mode = Mode.None;
            return;
        }

        _held += deltaTime;
        var landing = _mode == Mode.Surface ? FindSurface(chunks) : FindFree(chunks, _feet);
        if (landing is not { } feet
            || (!IsMeshed(chunks, feet) && _held < StreamingConfig.WaystoneArrivalTimeoutSeconds))
        {
            Hold();
            return;
        }

        _mode = Mode.None;
        _feet = feet;
        Hold();
    }

    private void Hold()
    {
        _heldEye = _feet + new Vector3(0f, Camera.EyeHeight + Lift, 0f);
        camera.TestTeleport(_heldEye, camera.Yaw, camera.Pitch);
        resetFall(_feet.Y);
    }

    /// <summary>Whether the chunk holding <paramref name="feet"/> has been meshed (an empty one counts), so a
    /// landing never stands the player in a void.</summary>
    internal static bool IsMeshed(ChunkManager chunks, Vector3 feet) =>
        chunks.ChunkAt(MathUtils.FloorToInt(feet.X), MathUtils.FloorToInt(feet.Y), MathUtils.FloorToInt(feet.Z)) is
            { IsMeshed: true };

    // The column's top standing spot: down from above the tallest tree the heightmap allows to the first height the
    // body collides at, or the first water surface. Null while any cell on the way is unloaded.
    private Vector3? FindSurface(ChunkManager chunks)
    {
        var sx = MathUtils.FloorToInt(_feet.X);
        var sz = MathUtils.FloorToInt(_feet.Z);
        var column = chunks.WorldSource.GetColumn(VoxelCoords.WorldToChunk(sx), VoxelCoords.WorldToChunk(sz));
        var heightmap = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(sx), VoxelCoords.WorldToLocal(sz))];
        var estimate = Math.Max(heightmap, WorldGenerationConfig.SeaLevel);
        var top = estimate + TreeGenerator.ReachUp + 2;

        // Above a spot already inside a solid (an overhang over the column), climb out first
        var start = FindFree(chunks, new Vector3(_feet.X, top, _feet.Z));
        if (start is not { } free)
            return null;

        for (var y = MathUtils.FloorToInt(free.Y) - 1; y > estimate - MaxSurfaceDepth; y--)
        {
            if (!BodyCellsLoaded(chunks, sx, y, sz))
                return null;
            if (chunks.TryGetCell(sx, y, sz, out var id, out _) && BlockType.IsFluid(id))
                return new Vector3(_feet.X, y + 1, _feet.Z);
            var feet = new Vector3(_feet.X, y, _feet.Z);
            if (Collides(chunks, feet))
                return SettleDown(chunks, feet with { Y = y + 1 });
        }

        return null;
    }

    // The first free height at or above feet (the spot itself when the body fits there), settled onto the floor it
    // climbed onto. Null while a cell the body touches on the way is unloaded.
    private static Vector3? FindFree(ChunkManager chunks, Vector3 feet)
    {
        var sx = MathUtils.FloorToInt(feet.X);
        var sz = MathUtils.FloorToInt(feet.Z);
        for (var lift = 0; lift <= MaxLiftBlocks; lift++)
        {
            var candidate = feet + new Vector3(0f, lift, 0f);
            var cellY = MathUtils.FloorToInt(candidate.Y);
            for (var y = cellY - 1; y <= cellY + 2; y++)
                if (!BodyCellsLoaded(chunks, sx, y, sz))
                    return null;
            if (Collides(chunks, candidate))
                continue;
            return lift == 0 ? candidate : SettleDown(chunks, candidate);
        }

        return feet;
    }

    // Lowers a free body in small steps until the next one would collide, so it rests on a partial floor
    private static Vector3 SettleDown(ChunkManager chunks, Vector3 feet)
    {
        for (var i = 0; i < 16; i++)
        {
            var lower = feet - new Vector3(0f, SettleStep, 0f);
            if (Collides(chunks, lower))
                break;
            feet = lower;
        }
        return feet;
    }

    private static bool Collides(ChunkManager chunks, Vector3 feet) =>
        BodyCollision.BodyCollides(feet + new Vector3(0f, Camera.EyeHeight + Lift, 0f), chunks);

    // Every cell of the 3x3 columns the body can reach at height y is loaded (the body is under a cell wide)
    private static bool BodyCellsLoaded(ChunkManager chunks, int x, int y, int z)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            if (!chunks.TryGetCell(x + dx, y, z + dz, out _, out _))
                return false;
        return true;
    }
}
