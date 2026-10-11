using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A gravity-affected block (sand, gravel) in flight: a full-size cube that falls under
/// gravity and collides with blocks axis by axis like the player, the item drops and primed
/// TNT, sinking slowly through water instead of floating. Once it rests it reports
/// <see cref="IsSettled"/>, and its owner writes it back as a block.
/// </summary>
public class FallingBlock
{
    // Slightly inside a full block so a cube spawned where a block just was rests in the freed
    // cell instead of clipping its solid neighbours
    public const float HalfExtent = 0.49f;

    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float GroundFriction = 8f;
    private const float CollisionStepSize = 0.05f;

    // A falling cube is heavier than an item drop: it does not float, it just sinks slowly
    private const float WaterSinkSpeed = -3f;
    private const float WaterDrag = 5f;

    // Seconds at rest before turning back into a block, so a cube still rolling off an edge
    // is not cemented mid-slide
    private const float SettleSeconds = 0.25f;

    // Seconds a motionless-but-unsettled cube is tolerated before it counts as wedged;
    // healthy cubes settle within a second of stopping.
    private const float StuckSeconds = 5f;

    // Horizontal shove a landing roll gives the cube, in blocks/s: enough to cross a whole
    // block top, so it walks off the pillar it landed on and keeps falling beside it
    private const float SlideNudgeSpeed = 2.5f;

    private static readonly (int X, int Z)[] SlideDirs =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    public Vector3 Position { get; private set; }

    /// <summary>The block that toppled, so the cube renders as itself.</summary>
    public byte BlockId { get; }

    /// <summary>Set when the cube toppled as part of a multi-cube column collapse (see
    /// <see cref="FallingBlockManager.TryTopple"/>): chain cubes stack on sand instead of
    /// sliding, so the column rebuilds bottom-up with nothing lost. A lone grain keeps the
    /// default and slumps off sand like stone.</summary>
    public bool ChainCollapse { get; set; }

    /// <summary>Whether the cube has rested long enough to be written back as a block.</summary>
    public bool IsSettled => _restTime >= SettleSeconds;

    /// <summary>Whether the cube has sat essentially motionless long enough to count as
    /// wedged: it will never settle on its own, so its owner should break it into drops.</summary>
    public bool IsStuck => _stillTime >= StuckSeconds;

    private Vector3 _velocity;
    private float _restTime;
    private bool _sliding;
    private bool _wasAirborne = true;

    /// <summary>Slide direction this cube committed to; a later landing keeps going straight
    /// while the way stays clear instead of wandering off on a fresh roll.</summary>
    private (int X, int Z)? _slideDir;

    private Vector3 _lastPosition;
    private float _stillTime;

    public FallingBlock(Vector3 position, byte blockId)
    {
        Position = position;
        BlockId = blockId;
        _lastPosition = position;
    }

    /// <summary>Current velocity in blocks per second.</summary>
    public Vector3 Velocity => _velocity;

    /// <summary>
    /// Adds <paramref name="impulse"/> (blocks per second) to the velocity: a blast's shove
    /// (<see cref="BlastImpulse"/>). The cube counts as airborne again, so it neither settles mid-flight
    /// nor keeps a slide it was committed to.
    /// </summary>
    public void Push(Vector3 impulse)
    {
        _velocity += impulse;
        _restTime = 0f;
        _sliding = false;
        _slideDir = null;
        _wasAirborne = true;
    }

    /// <summary>Advances the physics. Main thread only.</summary>
    public void Update(float deltaTime, ChunkManager chunks)
    {
        ApplyPhysics(deltaTime, chunks);

        if (Vector3.DistanceSquared(Position, _lastPosition) < 1e-6f)
            _stillTime += deltaTime;
        else
        {
            _stillTime = 0f;
            _lastPosition = Position;
        }
    }

    /// <summary>Whether the cube's body touches the world cell (x, y, z).</summary>
    public bool OverlapsCell(int x, int y, int z)
    {
        return Position.X - HalfExtent < x + 1f && Position.X + HalfExtent > x
            && Position.Y - HalfExtent < y + 1f && Position.Y + HalfExtent > y
            && Position.Z - HalfExtent < z + 1f && Position.Z + HalfExtent > z;
    }

    /// <summary>
    /// Falls under gravity and slides along blocks, axis by axis like the player, so the cube
    /// lands on top of the terrain instead of sinking through it.
    /// </summary>
    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f || chunks == null) return;

        var (fluidX, fluidY, fluidZ) = Position.BlockCell();
        var submerged = Position.Y < chunks.GetFluidSurfaceGlobal(fluidX, fluidY, fluidZ);

        if (submerged)
            _velocity.Y = MathUtils.Damp(_velocity.Y, WaterSinkSpeed, WaterDrag, deltaTime);
        else
            _velocity.Y = MathF.Max(_velocity.Y - Gravity * deltaTime, TerminalVelocity);

        var movement = _velocity * deltaTime;

        Position = MoveOnAxis(Position, movement.X, 0, chunks, out var hitX);
        Position = MoveOnAxis(Position, movement.Z, 2, chunks, out var hitZ);
        Position = MoveOnAxis(Position, movement.Y, 1, chunks, out var hitY);

        // Walls just stop that axis; a downward impact rolls the sand-sim slide below
        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        var landed = false;
        if (hitY)
        {
            if (_velocity.Y < 0f)
            {
                // Gravity reapplies every frame, so a resting cube micro-impacts forever:
                // only airtime followed by impact counts as a landing, or the slide would
                // re-roll every frame and the cube would wander instead of sliding straight
                landed = _wasAirborne;
                _velocity.Y = 0f;
            }
            else if (_velocity.Y > 0f)
            {
                _velocity.Y = 0f;
            }
        }

        if (landed)
        {
            _sliding = false;
            RollSlide(chunks);

            // A calm landing converts at once: the cube below is already a block again by the
            // time the next one arrives, so a collapsing tower rebuilds bottom-up instead of
            // the cubes piling into each other's cells while a settle timer runs down
            if (!_sliding && _velocity.X == 0f && _velocity.Z == 0f)
                _restTime = SettleSeconds;
        }

        // A sliding cube keeps its shove while crossing the block top; friction would kill a
        // 2.5 blocks/s nudge within centimetres, so it is skipped until the cube lands again
        if (hitY && _velocity.Y == 0f && !_sliding)
        {
            var damp = MathF.Max(0f, 1f - GroundFriction * deltaTime);
            _velocity.X *= damp;
            _velocity.Z *= damp;
            if (MathF.Abs(_velocity.X) < 0.01f) _velocity.X = 0f;
            if (MathF.Abs(_velocity.Z) < 0.01f) _velocity.Z = 0f;

            if (_velocity.X == 0f && _velocity.Z == 0f)
                _restTime += deltaTime;
            else
                _restTime = 0f;
        }
        else
        {
            _restTime = 0f;
        }

        // A slide that walks off its pillar drops straight into the cell beside it instead
        // of sailing on: the shove only ever crosses one block top, so anything left in the
        // air past the edge is overshoot, not momentum. hitY only fires on real Y motion, so
        // a coasting cube tests the support under its centre instead.
        if (_sliding && !hitY)
        {
            var (cx, cy, cz) = Position.BlockCell();
            if (!chunks.TryGetCell(cx, cy - 1, cz, out var under, out _)
                || !BlockType.IsSolid(under))
            {
                _velocity.X = 0f;
                _velocity.Z = 0f;
                _sliding = false;
            }
        }

        // A slide that ran into a wall stops dead but never leaves the pillar, so it settles
        // instead of hovering in sliding state forever
        if (_sliding && _velocity.X == 0f && _velocity.Z == 0f)
            _sliding = false;

        _wasAirborne = !hitY;
    }

    /// <summary>
    /// Sand-simulator slide: a cube landing on a support with exposed sides may keep going.
    /// The chance scales with how alone the support is — solid neighbours of the support
    /// around its own level, diagonals included, each remove an eighth: a buried support
    /// never slides, a lone pillar always does. The target side may be orthogonal or diagonal;
    /// once a direction is chosen it is kept across landings while it stays clear.
    /// </summary>
    private void RollSlide(ChunkManager chunks)
    {
        var (rx, ry, rz) = Position.BlockCell();
        var sy = ry - 1;

        if (!chunks.TryGetCell(rx, sy, rz, out var support, out _) || !BlockType.IsSolid(support))
        {
            _slideDir = null;
            return;
        }

        // Chain cubes stack on sand so the column rebuilds; a lone grain slumps instead
        // of growing a tower wherever it lands.
        if (ChainCollapse && BlockType.IsFalling(support))
        {
            _slideDir = null;
            return;
        }

        var dir = PickSlideDirection(rx, ry, rz, _slideDir,
            (x, y, z) => !chunks.TryGetCell(x, y, z, out var id, out _) || BlockType.IsSolid(id),
            Random.Shared);
        _slideDir = dir;
        if (!dir.HasValue) return;

        // Diagonal shoves are normalized so every slide crosses one block top at the same pace
        var (ex, ez) = dir.Value;
        var speed = SlideNudgeSpeed / MathF.Sqrt(ex * ex + ez * ez);
        _velocity.X = ex * speed;
        _velocity.Z = ez * speed;
        _sliding = true;
    }

    /// <summary>
    /// Picks where a cube resting at (rx, ry, rz) on solid support slides, or null when it
    /// stays. <paramref name="solidOrMissing"/> reports whether a cell is solid (unloaded
    /// counts as solid: it must dam the slide, not invite it off the world edge).
    /// A committed <paramref name="previous"/> direction is kept while its way stays clear;
    /// otherwise the (8 - neighbours) / 8 chance rolls and the pick is uniform over every
    /// clear side, corners included.
    /// </summary>
    public static (int X, int Z)? PickSlideDirection(int rx, int ry, int rz,
        (int X, int Z)? previous, Func<int, int, int, bool> solidOrMissing, Random random)
    {
        if (previous.HasValue && IsSlideTarget(rx, ry, rz, previous.Value, solidOrMissing))
            return previous;

        var sy = ry - 1;
        var neighbours = 0;
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            if (solidOrMissing(rx + dx, sy, rz + dz)) neighbours++;
        }

        if (neighbours >= 8) return null;
        if (random.NextSingle() >= (8 - neighbours) / 8f) return null;

        Span<(int X, int Z)> eligible = stackalloc (int X, int Z)[8];
        var count = 0;
        foreach (var (dx, dz) in SlideDirs)
        {
            if (IsSlideTarget(rx, ry, rz, (dx, dz), solidOrMissing))
                eligible[count++] = (dx, dz);
        }
        if (count == 0) return null;

        return eligible[random.Next(count)];
    }

    /// <summary>
    /// Whether the cube fits through the side cell at rest level and has room to drop into
    /// below it. A diagonal also needs its two neighbouring sides and the three cells above
    /// the move clear, so the cube never clips a corner it slides toward.
    /// </summary>
    private static bool IsSlideTarget(int rx, int ry, int rz, (int X, int Z) dir,
        Func<int, int, int, bool> solidOrMissing)
    {
        var (dx, dz) = dir;
        var sy = ry - 1;

        if (solidOrMissing(rx + dx, ry, rz + dz)
            || solidOrMissing(rx + dx, sy, rz + dz))
            return false;

        if (dx != 0 && dz != 0)
        {
            if (solidOrMissing(rx + dx, ry, rz)
                || solidOrMissing(rx, ry, rz + dz))
                return false;
            if (solidOrMissing(rx + dx, ry + 1, rz + dz)
                || solidOrMissing(rx + dx, ry + 1, rz)
                || solidOrMissing(rx, ry + 1, rz + dz))
                return false;
        }

        return true;
    }

    private static Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunks, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = MathUtils.ClampStep(remaining, CollisionStepSize);
            var candidate = OffsetAxis(position, axis, step);

            if (IsColliding(candidate, chunks))
            {
                collided = true;
                break;
            }

            position = candidate;
            remaining -= step;
        }

        return position;
    }

    private static Vector3 OffsetAxis(Vector3 value, int axis, float delta)
    {
        return axis switch
        {
            0 => new Vector3(value.X + delta, value.Y, value.Z),
            1 => new Vector3(value.X, value.Y + delta, value.Z),
            2 => new Vector3(value.X, value.Y, value.Z + delta),
            _ => value
        };
    }

    private static bool IsColliding(Vector3 center, ChunkManager chunks)
    {
        VoxelCoords.CellRange(center, HalfExtent, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            // Solid, not merely non-air: a falling cube drops through fluids and fragile
            // blocks like the player and the item drops do
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z)))
                return true;
        }

        return false;
    }
}
