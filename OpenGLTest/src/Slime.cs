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
    private const float WaterSinkSpeed = -2f;
    private const float WaterDrag = 4f;
    private const float LandSquashSeconds = 0.25f;

    // A hop rises a little over a block (v^2 / 2g with g = 28), so the slime can climb one step
    // on its way to the player without any special-cased step-up.
    private const float HopSpeed = 4f;
    private const float HopJumpSpeed = 8.5f;
    private const float HopIntervalSeconds = 1.2f;
    private const float HopIntervalJitter = 0.3f;

    /// <summary>Half the collision body's edge; a hair inside the 0.75-block drawn body so a
    /// slime resting on a freed cell does not clip its neighbours.</summary>
    public const float HalfExtent = 0.36f;

    private const float ContactDamageCooldownSeconds = 0.6f;

    private Vector3 _velocity;
    private float _hopTimer;
    private float _landPulse;
    private float _airBlend;
    private float _damageCooldown;
    private float _nextHop;

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

    /// <summary>Whether a touch may deal damage right now (airborne, off cooldown).</summary>
    public bool CanDealContactDamage => IsAirborne && _damageCooldown <= 0f;

    /// <summary>Vertical draw scale: stretched in the air, squashed for a moment on landing.</summary>
    public float VerticalScale => 1f + 0.12f * _airBlend - 0.18f * _landPulse;

    /// <summary>Horizontal draw scale, the volume-preserving counterpart of
    /// <see cref="VerticalScale"/>.</summary>
    public float HorizontalScale => 1f - (VerticalScale - 1f) * 0.5f;

    /// <summary>Applies a hit; the owner drops the loot and removes the slime once dead.</summary>
    public void TakeDamage(int amount)
    {
        if (amount <= 0) return;
        Health = Math.Max(0, Health - amount);
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
        _damageCooldown = MathF.Max(0f, _damageCooldown - deltaTime);

        ApplyPhysics(deltaTime, chunks);

        if (!IsGrounded) return;

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
        _velocity.X = toPlayer.X * HopSpeed;
        _velocity.Z = toPlayer.Z * HopSpeed;
        _velocity.Y = HopJumpSpeed;
        IsGrounded = false;

        // The model faces -Z at yaw 0, so this rotation points -Z at the player.
        Facing = MathF.Atan2(-toPlayer.X, -toPlayer.Z);
    }

    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        var feetY = Position.Y - HalfExtent;
        var submerged = feetY < chunks.GetFluidSurfaceGlobal(
            (int)MathF.Floor(Position.X), (int)MathF.Floor(feetY), (int)MathF.Floor(Position.Z));

        if (submerged)
            _velocity.Y = MathUtils.Damp(_velocity.Y, WaterSinkSpeed, WaterDrag, deltaTime);
        else
            _velocity.Y = MathF.Max(_velocity.Y - Gravity * deltaTime, TerminalVelocity);

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
