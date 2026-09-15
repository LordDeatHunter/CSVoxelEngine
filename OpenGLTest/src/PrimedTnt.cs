using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A primed TNT block: a full-size cube that falls under gravity and collides with blocks axis
/// by axis like the player and the item drops, ticking down its fuse until <see cref="TntManager"/>
/// detonates it. While primed it flashes white, faster as the fuse runs out.
/// </summary>
public class PrimedTnt
{
    /// <summary>Seconds from priming to detonation, set by whatever lit it.</summary>
    public float FuseDuration { get; }

    // Slightly inside a full block so a cube spawned where a block just was rests in the freed
    // cell instead of clipping its solid neighbours
    private const float HalfExtent = 0.49f;

    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float GroundFriction = 8f;
    private const float BounceDamping = 0.35f;
    private const float BounceThreshold = 4f;
    private const float CollisionStepSize = 0.05f;

    // A primed cube is heavier than an item drop: it does not float, it just sinks slowly
    private const float WaterSinkSpeed = -3f;
    private const float WaterDrag = 5f;

    public Vector3 Position { get; private set; }

    /// <summary>The block that primed, so the cube renders as whatever declared
    /// <c>onBreak: prime_tnt</c> instead of one hardcoded type.</summary>
    public byte BlockId { get; }

    private Vector3 _velocity;
    private float _age;

    /// <summary>Seconds left on the fuse.</summary>
    public float FuseRemaining { get; private set; }

    /// <summary>Whether the fuse has run out and this should detonate.</summary>
    public bool IsDue => FuseRemaining <= 0f;

    /// <summary>0 for a normal textured draw, 1 for full white (square-wave strobe).</summary>
    public float WhiteFlash
    {
        get
        {
            // Blink rate ramps from 4 to ~16 Hz as the fuse burns down
            var progress = 1f - MathUtils.Clamp01(FuseRemaining / FuseDuration);
            var rate = MathUtils.Lerp(4f, 16f, progress);
            return ((int)(_age * rate) & 1) == 0 ? 1f : 0f;
        }
    }

    /// <summary>Current edge length; the cube swells slightly as detonation nears.</summary>
    public float Scale
    {
        get
        {
            var progress = 1f - MathUtils.Clamp01(FuseRemaining / FuseDuration);
            return 1f + 0.1f * progress * (WhiteFlash == 1f ? 1f : 0f);
        }
    }

    public PrimedTnt(Vector3 position, byte blockId, float fuseSeconds)
    {
        Position = position;
        BlockId = blockId;
        FuseDuration = fuseSeconds;
        FuseRemaining = fuseSeconds;

        // Small random pop so the cube visibly ejects instead of appearing dead still, with an
        // upward bias so surface-primed TNT lands back on top rather than inside the floor
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var horizontalSpeed = 0.5f + Random.Shared.NextSingle();
        _velocity = new Vector3(
            MathF.Cos(angle) * horizontalSpeed,
            2f + Random.Shared.NextSingle() * 1.5f,
            MathF.Sin(angle) * horizontalSpeed);
    }

    /// <summary>Advances the fuse and the physics. Main thread only.</summary>
    public void Update(float deltaTime, ChunkManager chunks)
    {
        _age += deltaTime;
        FuseRemaining -= deltaTime;
        ApplyPhysics(deltaTime, chunks);
    }

    /// <summary>
    /// Falls under gravity and slides along blocks, axis by axis like the player, so primed TNT
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

        // Walls just stop that axis; the floor bounces light impacts and rests on hard ones
        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        if (hitY)
        {
            if (_velocity.Y < 0f)
            {
                // Underwater the impact is absorbed instead of bouncing
                var impact = -_velocity.Y;
                _velocity.Y = !submerged && impact >= BounceThreshold ? impact * BounceDamping : 0f;
            }
            else
            {
                _velocity.Y = 0f;
            }
        }

        // Scrub horizontal speed once settled so TNT doesn't slide forever on flat ground
        if (hitY && _velocity.Y == 0f)
        {
            var damp = MathF.Max(0f, 1f - GroundFriction * deltaTime);
            _velocity.X *= damp;
            _velocity.Z *= damp;
            if (MathF.Abs(_velocity.X) < 0.01f) _velocity.X = 0f;
            if (MathF.Abs(_velocity.Z) < 0.01f) _velocity.Z = 0f;
        }
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
            // Solid, not merely non-air: a primed cube falls through pebbles like the
            // player and the item drops do
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z)))
                return true;
        }

        return false;
    }
}
