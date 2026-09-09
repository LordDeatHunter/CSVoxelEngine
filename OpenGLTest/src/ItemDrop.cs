using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A collectable left behind by mining: usually a block, sometimes a pure item (see
/// <see cref="ItemType"/>). A drop pops out where the block was, falls under
/// gravity and rests on the terrain, spinning and breathing in scale, until the player comes
/// close enough — then it accelerates towards them and is collected on contact. Once latched
/// onto the player it flies straight and ignores terrain.
/// </summary>
public class ItemDrop
{
    /// <summary>Edge length of the floating cube, in blocks.</summary>
    public const float Size = 0.35f / 1.5f;

    /// <summary>Item sprites render larger than block cubes so they read at a distance.</summary>
    private const float ItemSizeMultiplier = 1.5f;

    /// <summary>Half the edge length; the drop collides as a cube this far from its centre.</summary>
    private const float HalfExtent = Size * 0.5f;

    private const float SpinRadiansPerSecond = 1.5f;

    // Gentle breathing, as a fraction of Size, over PulsePeriod seconds
    private const float PulseAmount = 0.08f;
    private const float PulsePeriod = 1.4f;

    // Speeds in blocks/s. The drop eases from a drift into a fast whip, so
    // being pulled in reads as deliberate rather than instant.
    private const float DriftSpeed = 2f;
    private const float SuckSpeed = 18f;
    private const float RampSeconds = 0.175f;

    // Close enough to count as collected, so a drop never hovers just short of the target
    private const float CollectDistance = 0.2f;

    // Idle physics (before the drop latches onto the player): gravity, ground friction and a
    // small bounce so drops settle onto the terrain instead of hovering where the block was
    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float GroundFriction = 8f;
    private const float BounceDamping = 0.35f;
    private const float BounceThreshold = 4f;
    private const float CollisionStepSize = 0.05f;

    // Buoyancy: instead of gravity, a submerged drop eases toward a rise proportional to how
    // far under the surface it is, so it climbs, barely overshoots and bobs at the top
    private const float FloatSpeed = 1.5f;
    private const float BuoyancyStiffness = 3f;
    private const float WaterDrag = 4f;
    private const float WaterFriction = 3f;
    // How hard a flow accelerates a floating drop, in blocks/s^2; against WaterFriction the
    // drift settles at roughly FlowPushAccel / WaterFriction blocks/s
    private const float FlowPushAccel = 4f;

    // A drop chasing a player faster than SuckSpeed is collected anyway rather than trailing
    // them forever
    private const float MaxAttractSeconds = 1.5f;

    // Grace period before a fresh drop can be pulled in. Without it a block mined at arm's
    // length is collected the same instant it appears and its spin is never seen.
    private const float PickupDelaySeconds = 0.4f;

    /// <summary>Block or item id this drop collects as (see <see cref="ItemType"/>).</summary>
    public byte BlockId { get; }
    public Vector3 Position { get; private set; }

    private Vector3 _velocity;
    private float _age;
    private float _attractElapsed;
    private bool _attracted;

    /// <summary>Whether the drop has landed and is resting on the terrain.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Whether the drop has committed to flying at the player.</summary>
    public bool IsAttracted => _attracted;

    /// <summary>Continuous spin about the Y axis, in radians.</summary>
    public float Spin => _age * SpinRadiansPerSecond;

    /// <summary>Current edge length, including the idle pulse; items render 1.5x.</summary>
    public float Scale => VisualSize * (1f + PulseAmount * MathUtils.Oscillate(_age, PulsePeriod));

    // Collision stays at the shared Size: the item boost is visual only
    private float VisualSize => Size * (ItemType.IsItem(BlockId) ? ItemSizeMultiplier : 1f);

    public ItemDrop(byte blockId, Vector3 position, Vector3? toss = null)
    {
        BlockId = blockId;
        Position = position;

        // Small random pop so the drop visibly ejects instead of appearing dead still, with an
        // upward bias so a surface-mined block lands back on top rather than inside the floor.
        // A throw adds its own velocity on top.
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var horizontalSpeed = 0.5f + Random.Shared.NextSingle();
        _velocity = new Vector3(
            MathF.Cos(angle) * horizontalSpeed,
            2f + Random.Shared.NextSingle() * 1.5f,
            MathF.Sin(angle) * horizontalSpeed);
        if (toss.HasValue)
            _velocity += toss.Value;
    }

    /// <summary>
    /// Advances the drop, returning true once it reaches <paramref name="target"/> and should be
    /// collected. While idle the drop falls under gravity and collides with blocks;
    /// <paramref name="inRange"/> latches attraction on, so a drop already being
    /// pulled keeps coming even if the player backs away.
    /// </summary>
    public bool Update(float deltaTime, Vector3 target, bool inRange, ChunkManager chunks)
    {
        _age += deltaTime;

        if (!_attracted)
        {
            ApplyPhysics(deltaTime, chunks);
            if (!inRange || _age < PickupDelaySeconds) return false;
            _attracted = true;
        }

        _attractElapsed += deltaTime;
        if (_attractElapsed >= MaxAttractSeconds) return true;

        var toTarget = target - Position;
        var distance = toTarget.Length();
        if (distance <= CollectDistance) return true;

        var speed = MathUtils.Lerp(DriftSpeed, SuckSpeed,
            MathUtils.EaseInCubic(_attractElapsed / RampSeconds));
        var step = speed * deltaTime;

        // Collect rather than overshoot, so a fast drop never orbits the player
        if (step >= distance) return true;

        Position += toTarget / distance * step;
        return false;
    }

    /// <summary>
    /// Falls under gravity and slides along blocks, axis by axis like the player, so drops land
    /// on top of the terrain instead of sinking through it.
    /// </summary>
    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f || chunks == null) return;

        // The bottom of the cube decides: a drop resting on the sea floor with its top out of
        // the water should not still be pushed up
        var bottomY = Position.Y - HalfExtent;
        var surfaceY = chunks.GetFluidSurfaceGlobal(
            (int)MathF.Floor(Position.X), (int)MathF.Floor(bottomY), (int)MathF.Floor(Position.Z));
        var floating = bottomY < surfaceY;

        if (floating)
        {
            var targetY = Math.Clamp((surfaceY - bottomY) * BuoyancyStiffness, -FloatSpeed, FloatSpeed);
            _velocity.Y = MathUtils.Damp(_velocity.Y, targetY, WaterDrag, deltaTime);

            // Flowing water carries the drop downstream. Applied before the damping below, which
            // then caps the drift instead of letting it build up indefinitely.
            var flow = chunks.GetFluidFlowGlobal(
                (int)MathF.Floor(Position.X), (int)MathF.Floor(bottomY), (int)MathF.Floor(Position.Z));
            _velocity.X += flow.X * FlowPushAccel * deltaTime;
            _velocity.Z += flow.Z * FlowPushAccel * deltaTime;

            // Water also kills the sideways pop, so a drop thrown into a pond stops drifting
            var drag = MathF.Exp(-WaterFriction * deltaTime);
            _velocity.X *= drag;
            _velocity.Z *= drag;
        }
        else
        {
            _velocity.Y = MathF.Max(_velocity.Y - Gravity * deltaTime, TerminalVelocity);
        }

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
                // Water absorbs the impact: a sinking drop settles on the bottom rather than
                // bouncing back up through the surface
                var impact = -_velocity.Y;
                _velocity.Y = !floating && impact >= BounceThreshold ? impact * BounceDamping : 0f;
                IsGrounded = _velocity.Y == 0f;
            }
            else
            {
                _velocity.Y = 0f;
            }
        }
        else
        {
            IsGrounded = false;
        }

        // Scrub horizontal speed once settled so drops don't slide forever on flat ground
        if (IsGrounded)
        {
            var damp = MathF.Max(0f, 1f - GroundFriction * deltaTime);
            _velocity.X *= damp;
            _velocity.Z *= damp;
            if (MathF.Abs(_velocity.X) < 0.01f) _velocity.X = 0f;
            if (MathF.Abs(_velocity.Z) < 0.01f) _velocity.Z = 0f;
        }
    }

    private Vector3 MoveOnAxis(Vector3 start, float delta, int axis, ChunkManager chunks, out bool collided)
    {
        collided = false;
        if (MathF.Abs(delta) < float.Epsilon)
            return start;

        var position = start;
        var remaining = delta;

        while (MathF.Abs(remaining) > float.Epsilon)
        {
            var step = Math.Clamp(remaining, -CollisionStepSize, CollisionStepSize);
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
        var minX = (int)MathF.Floor(center.X - HalfExtent);
        var minY = (int)MathF.Floor(center.Y - HalfExtent);
        var minZ = (int)MathF.Floor(center.Z - HalfExtent);
        var maxX = (int)MathF.Floor(center.X + HalfExtent);
        var maxY = (int)MathF.Floor(center.Y + HalfExtent);
        var maxZ = (int)MathF.Floor(center.Z + HalfExtent);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z)))
                return true;
        }

        return false;
    }
}
