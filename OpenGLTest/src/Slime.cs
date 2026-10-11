using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A slime: a gel cube that hops toward the player and only hurts on contact while it is off the
/// ground. Grounded contact is harmless — the player shoves it instead. It falls and collides
/// axis by axis like the falling cubes and primed TNT, sinks slowly through water, faces the
/// player, and squashes on landing / stretches in the air. Its <see cref="Size"/> scales the body,
/// health, contact damage and loot (<see cref="SlimeSize"/>). Main thread only.
/// </summary>
public sealed class Slime : Mob
{
    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float CollisionStepSize = 0.05f;
    private const float GroundFriction = 8f;

    // Squash and stretch ride one under-damped spring (value = vertical scale offset), so a landing
    // rings out as a jiggle instead of easing back. The rest target breathes a little; a hop first
    // crouches (the windup) and launches with a stretch impulse.
    private const float SquashFrequency = 3.4f;
    private const float SquashDamping = 0.28f;
    private const float WindupSeconds = 0.2f;
    private const float WindupSquash = -0.26f;
    private const float AirStretch = 0.14f;
    private const float LaunchKick = 3.2f;
    private const float LandKickPerSpeed = 0.55f;
    private const float BreathAmplitude = 0.022f;
    private const float BreathRate = 2.3f;
    private const float TurnRate = 16f;

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

    /// <summary>Half the collision body's edge at size 1; a hair inside the 0.75-block drawn body
    /// so a slime resting on a freed cell does not clip its neighbours. A larger slime's is this
    /// times its <see cref="SizeScale"/> (<see cref="HalfExtentFor"/>).</summary>
    public const float HalfExtent = 0.36f;

    private const float ContactDamageCooldownSeconds = 0.6f;

    // Hit flash: snapped to full on TakeDamage, then fades linearly back to unflashed over
    // this long. Read by the entity pass as a per-slime uHitFlash tint (non-emissive red).
    private const float HitFlashSeconds = 0.25f;

    private Vector3 _velocity;
    private float _hopTimer;
    private Spring _squash = new(SquashFrequency, SquashDamping);
    private readonly float _windupSeconds;
    private float _windup;
    private Vector3 _windupTarget;
    private float _clock;
    private float _damageCooldown;
    private float _nextHop;
    private bool _inWater;
    private bool _splitPending;

    /// <summary>A slime of <paramref name="size"/> (clamped into <see cref="SlimeSize.Min"/>..
    /// <see cref="SlimeSize.Max"/>) at full health; <paramref name="position"/> is the body centre,
    /// <see cref="HalfExtentFor"/> that size above the feet.</summary>
    public Slime(EntityType type, Vector3 position, int size = SlimeSize.Min) : base(type, position)
    {
        Size = SlimeSize.Clamp(size);
        SizeScale = SlimeSize.Scale(Size);
        Health = MaxHealth;
        _nextHop = HopIntervalSeconds;
        // A larger slime crouches longer before it hops (SlimeSize.WindupScale)
        _windupSeconds = WindupSeconds * SlimeSize.WindupScale(Size);
    }

    /// <summary>The slime's size, fixed for its life; a split spawns smaller slimes instead.</summary>
    public int Size { get; }

    /// <summary>How much larger than a size-1 slime the body is drawn and collides.</summary>
    public float SizeScale { get; }

    /// <summary>The collision body's half edge of a slime of <paramref name="size"/>, which is also
    /// how far its body centre sits above its feet.</summary>
    public static float HalfExtentFor(int size) => HalfExtent * SlimeSize.Scale(size);

    /// <inheritdoc />
    public override int MaxHealth => SlimeSize.Health(Type.MaxHealth, Size);

    /// <summary>Damage one airborne touch deals, scaled by the size.</summary>
    public int ContactDamage => SlimeSize.ContactDamage(Size);

    /// <summary>How many times the death rolls the kind's loot table, scaled by the size.</summary>
    public int LootRolls => SlimeSize.LootRolls(Size);

    /// <inheritdoc />
    public override float BodyHalfExtent => HalfExtent * SizeScale;

    /// <inheritdoc />
    public override float BodyHalfHeight => HalfExtent * SizeScale;

    /// <inheritdoc />
    public override float HeadAboveCentre => BodyHalfHeight * VerticalScale;

    /// <summary>Whether the slime currently rests on the ground.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Off the ground — the only time a touch hurts.</summary>
    public bool IsAirborne => !IsGrounded;

    /// <summary>Whether a touch may deal damage right now: airborne (not merely floating in
    /// water), and past the per-hop cooldown.</summary>
    public bool CanDealContactDamage => !IsDying && IsAirborne && !_inWater && _damageCooldown <= 0f;

    /// <inheritdoc />
    public override float DeathSeconds => 0.45f;

    /// <summary>The shared safe fall plus the apex of this slime's own land hop (v² / 2g), so a hop on
    /// flat ground, however big the slime, never hurts it; a fall off a cliff past that still does.</summary>
    public override float SafeFallBlocks
    {
        get
        {
            var lift = HopVelocity(Vector3.UnitX, inWater: false, Size).Y;
            return base.SafeFallBlocks + lift * lift / (2f * Gravity);
        }
    }

    /// <summary>Whether the slime is crouching for a hop it is about to launch.</summary>
    public bool IsWindingUp => _windup > 0f;

    /// <summary>Vertical draw scale: the spring's squash/stretch, the death pop and the spawn pop
    /// (about 1 at rest; the size's <see cref="SizeScale"/> multiplies on top).</summary>
    public float VerticalScale => (1f + _squash.Value - HitKick * 0.04f) * LifeScale * DeathStretch;

    /// <summary>Horizontal draw scale, the volume-preserving counterpart of the squash.</summary>
    public float HorizontalScale => (1f - _squash.Value * 0.5f + HitKick * 0.02f) * LifeScale * DeathSpread;

    // A fresh slime pops in with an overshoot; a dying one balloons wide and flat, then shrinks away.
    private float LifeScale =>
        MathUtils.EaseOutBack(SpawnProgress) * (1f - MathUtils.EaseInCubic(MathUtils.InverseLerp(0.55f, 1f, DeathProgress)));

    private float DeathStretch => 1f + 0.35f * MathUtils.Pulse(DeathProgress * 2.2f, 0.4f) - 0.55f * MathUtils.SmoothStep(0.25f, 0.8f, DeathProgress);

    private float DeathSpread => 1f + 0.45f * MathUtils.SmoothStep(0.15f, 0.7f, DeathProgress);

    /// <summary>Records that contact damage just landed, starting the per-hop cooldown.</summary>
    public void NotifyContactDamage() => _damageCooldown = ContactDamageCooldownSeconds;

    /// <summary>Applies the hit, and marks a split when it crossed the half line while the slime
    /// survived (<see cref="SlimeSize.SplitsOnHit"/>); the owning manager spawns the children on its
    /// next update (<see cref="TakeSplit"/>). Only the authoritative simulation deals damage, so a
    /// client's mirror, whose health arrives from its server, never splits.</summary>
    public override void TakeDamage(int amount)
    {
        var before = Health;
        base.TakeDamage(amount);
        if (SlimeSize.SplitsOnHit(before, Health, MaxHealth, Size))
            _splitPending = true;
    }

    /// <summary>True once after a hit that split the slime, then false until another one does.</summary>
    internal bool TakeSplit()
    {
        if (!_splitPending) return false;
        _splitPending = false;
        return true;
    }

    /// <summary>Launches a slime just split off a larger one: a horizontal shove along
    /// <paramref name="horizontalDirection"/> plus a small upward <paramref name="lift"/>.</summary>
    internal void Pop(Vector3 horizontalDirection, float speed, float lift)
    {
        // The split's own launch, not a shove: the child's size does not damp it
        SetHorizontalVelocity(horizontalDirection, speed);
        _velocity.Y = lift;
        IsGrounded = false;
    }

    /// <summary>Sets the horizontal shove a grounded player contact or a hit imparts, leaving vertical
    /// motion alone. Setting (not adding) keeps a sustained touch from accelerating the slime. A larger
    /// slime is pushed less (<see cref="SlimeSize.ShoveSpeed"/>).</summary>
    public override void Shove(Vector3 horizontalDirection, float speed) =>
        SetHorizontalVelocity(horizontalDirection, SlimeSize.ShoveSpeed(speed, Size));

    private void SetHorizontalVelocity(Vector3 horizontalDirection, float speed)
    {
        if (horizontalDirection.LengthSquared() < 1e-6f) return;
        horizontalDirection.Y = 0f;
        horizontalDirection = Vector3.Normalize(horizontalDirection);
        _velocity.X = horizontalDirection.X * speed;
        _velocity.Z = horizontalDirection.Z * speed;
    }

    /// <inheritdoc />
    public override Vector3 Velocity => _velocity;

    /// <inheritdoc />
    protected override void SetVelocity(Vector3 velocity) => _velocity = velocity;

    /// <summary>Advances the hop AI and the physics. <paramref name="playerPosition"/> is the
    /// chased player's eye; the slime hops toward the player's feet so it climbs toward them.
    /// Null (nobody to chase) still runs the physics but never hops.</summary>
    public void Update(float deltaTime, ChunkManager chunks, Vector3? playerPosition)
    {
        if (deltaTime <= 0f || chunks == null) return;

        _clock += deltaTime;
        TickLife(deltaTime);
        _hitFlash = MathF.Max(0f, _hitFlash - deltaTime / HitFlashSeconds);
        _damageCooldown = MathF.Max(0f, _damageCooldown - deltaTime);

        var wasGrounded = IsGrounded;
        var fallSpeed = -_velocity.Y;
        ApplyPhysics(deltaTime, chunks);
        UpdateSquash(deltaTime, wasGrounded, fallSpeed);

        // Hop while grounded, or while floating in water where the buoyancy and drag applied
        // above damp the hop into a weak paddle at the player instead of a full leap.
        if (!IsGrounded && !_inWater) { _windup = 0f; return; }

        if (_windup > 0f)
        {
            // Crouching: turn toward the target, then launch when the crouch is done.
            var toTarget = _windupTarget - Position;
            TurnToward(MathF.Atan2(-toTarget.X, -toTarget.Z), TurnRate, deltaTime);
            _windup -= deltaTime;
            if (_windup <= 0f)
            {
                _windup = 0f;
                HopToward(_windupTarget);
            }
            return;
        }

        if (IsDying) return;
        if (playerPosition is not { } target) return;

        _hopTimer += deltaTime;
        if (_hopTimer < _nextHop) return;

        _hopTimer = 0f;
        _nextHop = HopIntervalSeconds + (Random.Shared.NextSingle() - 0.5f) * 2f * HopIntervalJitter;
        _windup = _windupSeconds;
        _windupTarget = target;
    }

    private void UpdateSquash(float deltaTime, bool wasGrounded, float fallSpeed)
    {
        // A landing hits the spring downward in proportion to the fall; leaving the ground is
        // kicked up by HopToward. Standing still never re-squashes.
        if (!wasGrounded && IsGrounded)
        {
            _squash.Impulse(-MathF.Min(fallSpeed, 20f) * LandKickPerSpeed);
            NoteLanding(fallSpeed, Gravity, _inWater);
        }

        float target;
        if (_windup > 0f) target = WindupSquash * MathUtils.EaseOutCubic(1f - _windup / _windupSeconds);
        else if (!IsGrounded && !_inWater) target = AirStretch;
        else target = BreathAmplitude * MathF.Sin(_clock * BreathRate);

        _squash.Step(target, deltaTime);
    }

    private void HopToward(Vector3 playerPosition)
    {
        var toPlayer = new Vector3(playerPosition.X - Position.X, 0f, playerPosition.Z - Position.Z);
        if (toPlayer.LengthSquared() < 1e-6f) return;

        toPlayer = Vector3.Normalize(toPlayer);
        _velocity = HopVelocity(toPlayer, _inWater, Size);
        IsGrounded = false;
        _squash.Impulse(LaunchKick);

        // The model faces -Z at yaw 0, so this rotation points -Z at the player.
        Facing = MathF.Atan2(-toPlayer.X, -toPlayer.Z);
    }

    /// <summary>
    /// Launch velocity for a hop along the unit horizontal <paramref name="direction"/>. A land
    /// hop uses the full vertical lift; a water hop keeps the same total speed but halves the
    /// vertical component and moves the rest into the horizontal, so it reads as a low forward
    /// skim — a jumping slide. A larger <paramref name="size"/> launches faster on both axes
    /// (<see cref="SlimeSize.HopLift"/>, <see cref="SlimeSize.HopReach"/>), so it hops higher and farther.
    /// </summary>
    internal static Vector3 HopVelocity(Vector3 direction, bool inWater, int size = SlimeSize.Min)
    {
        var hopSpeed = HopSpeed * SlimeSize.HopReach(size);
        var jumpSpeed = HopJumpSpeed * SlimeSize.HopLift(size);
        if (!inWater)
            return new Vector3(direction.X * hopSpeed, jumpSpeed, direction.Z * hopSpeed);

        var totalSpeed = MathF.Sqrt(hopSpeed * hopSpeed + jumpSpeed * jumpSpeed);
        var vertical = jumpSpeed * WaterHopVerticalFactor;
        var horizontal = MathF.Sqrt(MathF.Max(0f, totalSpeed * totalSpeed - vertical * vertical));
        return new Vector3(direction.X * horizontal, vertical, direction.Z * horizontal);
    }

    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        var feetY = Position.Y - BodyHalfHeight;
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
        var half = BodyHalfExtent;

        Position = MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        Position = MoveOnAxis(Position, movement.Z, 2, half, chunks, out var hitZ);
        Position = MoveOnAxis(Position, movement.Y, 1, half, chunks, out var hitY);

        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;

        if (hitY)
        {
            if (movement.Y < 0f)
            {
                IsGrounded = true;
            }
            _velocity.Y = 0f;
        }
        else
        {
            IsGrounded = false;
        }
    }

    private static Vector3 MoveOnAxis(Vector3 start, float delta, int axis, float halfExtent, ChunkManager chunks,
        out bool collided)
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

            if (IsColliding(candidate, halfExtent, chunks))
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

    /// <summary>Whether a slime body of <paramref name="halfExtent"/> centred at
    /// <paramref name="center"/> overlaps a solid cell (unloaded cells read as air).</summary>
    internal static bool IsColliding(Vector3 center, float halfExtent, ChunkManager chunks)
    {
        VoxelCoords.CellRange(center, halfExtent, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

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
