using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A slime: a gel cube that hops toward the player and only hurts on contact while it is off the
/// ground. Grounded contact is harmless — the player shoves it instead. It falls and collides
/// axis by axis like the falling cubes and primed TNT, sinks slowly through water, faces the
/// player, and squashes on landing / stretches in the air. Main thread only.
/// </summary>
public sealed class Slime
{
    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float CollisionStepSize = 0.05f;
    private const float GroundFriction = 8f;
    private const float LandSquashSeconds = 0.25f;

    // Buoyancy, tuned like the item drops: instead of gravity a floating slime eases toward a
    // rise proportional to how far under the surface it is, so it climbs, barely overshoots and
    // bobs at the top. Water drags its sideways hop away and shrinks the hop itself.
    private const float FloatSpeed = 1.5f;
    private const float BuoyancyStiffness = 3f;
    private const float WaterDrag = 4f;
    private const float WaterFriction = 3f;

    // In water the hop keeps a land hop's total speed but halves its vertical share, moving the
    // rest into the horizontal, so the slime skims forward low and roughly twice as fast.
    private const float WaterHopVerticalFactor = 0.5f;

    // A hop rises a little over a block (v^2 / 2g with g = 28), so the slime can climb one step
    // on its way to the player without any special-cased step-up. Both launch speeds carry the
    // distance factor so a flat hop's range (2*H*V/g) grows by its square: sqrt(1.5) is 1.5x.
    private const float HopDistanceFactor = 1.2247449f; // sqrt(1.5)
    private const float HopSpeed = 4f * HopDistanceFactor;
    private const float HopJumpSpeed = 8.5f * HopDistanceFactor;
    private const float HopIntervalSeconds = 1.2f;
    private const float HopIntervalJitter = 0.3f;

    /// <summary>Half the collision body's edge; a hair inside the 0.75-block drawn body so a
    /// slime resting on a freed cell does not clip its neighbours.</summary>
    public const float HalfExtent = 0.36f;

    private const float ContactDamageCooldownSeconds = 0.6f;

    // Hit flash: snapped to full on TakeDamage, then fades linearly back to unflashed over
    // this long. Read by the entity pass as a per-slime uHitFlash tint (non-emissive red).
    private const float HitFlashSeconds = 0.25f;

    private Vector3 _velocity;
    private float _hopTimer;
    private float _landPulse;
    private float _airBlend;
    private float _damageCooldown;
    private float _hitFlash;
    private float _nextHop;
    private bool _inWater;

    public Slime(EntityType type, Vector3 position)
    {
        Type = type;
        Position = position;
        Health = type.MaxHealth;
        _nextHop = HopIntervalSeconds;
    }

    public EntityType Type { get; }

    /// <summary>Body centre; the feet sit <see cref="HalfExtent"/> below it.</summary>
    public Vector3 Position { get; private set; }

    public int Health { get; private set; }

    public bool IsDead => Health <= 0;

    /// <summary>Whether the slime currently rests on the ground.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Off the ground — the only time a touch hurts.</summary>
    public bool IsAirborne => !IsGrounded;

    /// <summary>Yaw in radians the body faces (toward the player); the model faces -Z at 0.</summary>
    public float Facing { get; private set; }

    /// <summary>Whether a touch may deal damage right now: airborne (not merely floating in
    /// water), and past the per-hop cooldown.</summary>
    public bool CanDealContactDamage => IsAirborne && !_inWater && _damageCooldown <= 0f;

    /// <summary>Vertical draw scale: stretched in the air, squashed for a moment on landing.</summary>
    public float VerticalScale => 1f + 0.12f * _airBlend - 0.18f * _landPulse;

    /// <summary>Horizontal draw scale, the volume-preserving counterpart of
    /// <see cref="VerticalScale"/>.</summary>
    public float HorizontalScale => 1f - (VerticalScale - 1f) * 0.5f;

    /// <summary>Hit flash amount, 1 right after <see cref="TakeDamage"/> fading to 0 over
    /// <c>HitFlashSeconds</c>. The entity pass tints the skin red by this (non-emissive).</summary>
    public float HitFlash => _hitFlash;

    /// <summary>Applies a hit; the owner drops the loot and removes the slime once dead.</summary>
    public void TakeDamage(int amount)
    {
        if (amount <= 0) return;
        Health = Math.Max(0, Health - amount);
        _hitFlash = 1f;
    }

    /// <summary>Records that contact damage just landed, starting the per-hop cooldown.</summary>
    public void NotifyContactDamage() => _damageCooldown = ContactDamageCooldownSeconds;

    /// <summary>Sets the horizontal shove a grounded player contact imparts, leaving vertical
    /// motion alone. Setting (not adding) keeps a sustained touch from accelerating the slime.</summary>
    public void Shove(Vector3 horizontalDirection, float speed)
    {
        if (horizontalDirection.LengthSquared() < 1e-6f) return;
        horizontalDirection.Y = 0f;
        horizontalDirection = Vector3.Normalize(horizontalDirection);
        _velocity.X = horizontalDirection.X * speed;
        _velocity.Z = horizontalDirection.Z * speed;
    }

    /// <summary>Advances the hop AI and the physics. <paramref name="playerPosition"/> is the
    /// camera eye; the slime hops toward the player's feet so it climbs toward them.</summary>
    public void Update(float deltaTime, ChunkManager chunks, Vector3 playerPosition)
    {
        if (deltaTime <= 0f || chunks == null) return;

        _airBlend = MathUtils.Damp(_airBlend, IsGrounded ? 0f : 1f, 10f, deltaTime);
        _landPulse = MathF.Max(0f, _landPulse - deltaTime / LandSquashSeconds);
        _hitFlash = MathF.Max(0f, _hitFlash - deltaTime / HitFlashSeconds);
        _damageCooldown = MathF.Max(0f, _damageCooldown - deltaTime);

        ApplyPhysics(deltaTime, chunks);

        // Hop while grounded, or while floating in water where the buoyancy and drag applied
        // above damp the hop into a weak paddle at the player instead of a full leap.
        if (!IsGrounded && !_inWater) return;

        _hopTimer += deltaTime;
        if (_hopTimer < _nextHop) return;

        _hopTimer = 0f;
        _nextHop = HopIntervalSeconds + (Random.Shared.NextSingle() - 0.5f) * 2f * HopIntervalJitter;
        HopToward(playerPosition);
    }

    private void HopToward(Vector3 playerPosition)
    {
        var toPlayer = new Vector3(playerPosition.X - Position.X, 0f, playerPosition.Z - Position.Z);
        if (toPlayer.LengthSquared() < 1e-6f) return;

        toPlayer = Vector3.Normalize(toPlayer);
        _velocity = HopVelocity(toPlayer, _inWater);
        IsGrounded = false;

        // The model faces -Z at yaw 0, so this rotation points -Z at the player.
        Facing = MathF.Atan2(-toPlayer.X, -toPlayer.Z);
    }

    /// <summary>
    /// Launch velocity for a hop along the unit horizontal <paramref name="direction"/>. A land
    /// hop uses the full vertical lift; a water hop keeps the same total speed but halves the
    /// vertical component and moves the rest into the horizontal, so it reads as a low forward
    /// skim — a jumping slide.
    /// </summary>
    internal static Vector3 HopVelocity(Vector3 direction, bool inWater)
    {
        if (!inWater)
            return new Vector3(direction.X * HopSpeed, HopJumpSpeed, direction.Z * HopSpeed);

        var totalSpeed = MathF.Sqrt(HopSpeed * HopSpeed + HopJumpSpeed * HopJumpSpeed);
        var vertical = HopJumpSpeed * WaterHopVerticalFactor;
        var horizontal = MathF.Sqrt(MathF.Max(0f, totalSpeed * totalSpeed - vertical * vertical));
        return new Vector3(direction.X * horizontal, vertical, direction.Z * horizontal);
    }

    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        var feetY = Position.Y - HalfExtent;
        var feetCell = new Vector3(Position.X, feetY, Position.Z).BlockCell();
        var surfaceY = chunks.GetFluidSurfaceGlobal(feetCell.X, feetCell.Y, feetCell.Z);
        var floating = feetY < surfaceY;
        _inWater = floating;

        if (floating)
        {
            var targetY = Math.Clamp((surfaceY - feetY) * BuoyancyStiffness, -FloatSpeed, FloatSpeed);
            _velocity.Y = MathUtils.Damp(_velocity.Y, targetY, WaterDrag, deltaTime);

            // Water scrubs the sideways motion so a floating slime drifts and bobs rather than
            // coasting: the same treatment the item drops get.
            var drag = MathF.Exp(-WaterFriction * deltaTime);
            _velocity.X *= drag;
            _velocity.Z *= drag;
        }
        else
        {
            _velocity.Y = MathF.Max(_velocity.Y - Gravity * deltaTime, TerminalVelocity);
        }

        // Ground friction scrubs the shove off once the slime is standing, so a poke slides it a
        // short way and stops instead of launching it across the field.
        if (IsGrounded)
        {
            var damp = MathF.Max(0f, 1f - GroundFriction * deltaTime);
            _velocity.X *= damp;
            _velocity.Z *= damp;
            if (MathF.Abs(_velocity.X) < 0.01f) _velocity.X = 0f;
            if (MathF.Abs(_velocity.Z) < 0.01f) _velocity.Z = 0f;
        }

        var movement = _velocity * deltaTime;

        Position = MoveOnAxis(Position, movement.X, 0, chunks, out var hitX);
        Position = MoveOnAxis(Position, movement.Z, 2, chunks, out var hitZ);
        Position = MoveOnAxis(Position, movement.Y, 1, chunks, out var hitY);

        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        if (hitY)
        {
            if (movement.Y < 0f)
            {
                // Landing only counts from a real fall, so standing still does not re-squash
                if (!IsGrounded) _landPulse = 1f;
                IsGrounded = true;
            }
            _velocity.Y = 0f;
        }
        else
        {
            IsGrounded = false;
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
            if (BlockType.IsSolid(chunks.GetBlockGlobal(x, y, z)))
                return true;
        }

        return false;
    }
}
