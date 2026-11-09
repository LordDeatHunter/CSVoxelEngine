using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities;

/// <summary>
/// A walking or flying mob animated from the Blockbench clips in its model through an
/// <see cref="EntityAnimator"/>: the goblin and the hell mobs. Owns what they share: the body
/// physics (walking toward a move with a damped velocity, an auto-jump over a one-block step,
/// floating and paddling in water, landings; or, for a kind built with a <see cref="FlightTuning"/>,
/// steered flight with no gravity, <see cref="Fly"/>), the facing that turns toward a goal, the death,
/// spawn and hurt clips, a client mirror's animation, and the procedural body language the clips
/// cannot give (<see cref="BodyEffect"/>). A subclass decides the move and its attacks each frame
/// and hands them to <see cref="Walk"/> or <see cref="Fly"/>. Main thread only.
/// </summary>
public abstract class RiggedMob : Mob
{
    // Optional clips: absent ones simply play nothing, the procedural BodyEffect still shows.
    public const string HurtClip = "hurt";
    public const string DeathClip = "death";
    public const string SpawnClip = "spawn";

    // Damp rate of the walking velocity toward the chosen move, so the body speeds up and turns
    // over a few frames instead of snapping to full speed.
    private const float MoveAccel = 12f;

    // Hit flash: snapped to full on TakeDamage, then fades linearly back over this long.
    private const float HitFlashSeconds = 0.25f;

    // Body language drawn on top of the clips (see BodyEffect): a forward lean that follows the
    // walk, a flinch on a hit, a fall backward on death and the spawn pop.
    private const float TurnRate = 14f;
    private const float LeanPerSpeed = 0.045f;
    private const float MaxLean = 0.2f;
    private const float HitLeanPerKick = 1.1f;
    private const float DeathFallRadians = 1.45f;
    private const float WaterLean = 0.12f;

    // A flyer pitches its nose up while it climbs (and down in a dive) and banks into a turn: in a
    // turn its velocity lags its facing, so the sideways part of the velocity tells which way it turns.
    private const float ClimbPitchPerSpeed = 0.06f;
    private const float MaxFlightPitch = 0.45f;
    private const float BankPerSpeed = 0.12f;
    private const float MaxBank = 0.5f;
    private const float FlightLeanRate = 6f;

    private Vector3 _velocity;
    private bool _inWater;
    private float _lean;
    private float _bank;
    private bool _facingKnown;
    private bool _deathPlayed;
    private bool _hitWall;
    private float _flightClock;
    private readonly float _bobPhase;

    /// <param name="walkReferenceSpeed">The speed the walk (or fly) clip is authored at, which scales its playback.</param>
    /// <param name="spawnIn">Plays the spawn clip and the pop-in; false starts settled.</param>
    /// <param name="flight">Makes the kind a flyer (<see cref="Fly"/>, the <see cref="EntityAnimator.FlyClip"/>
    /// locomotion clip); null walks.</param>
    protected RiggedMob(EntityType type, Vector3 position, float walkReferenceSpeed, bool spawnIn,
        FlightTuning? flight = null) : base(type, position)
    {
        flight?.Validate();
        Flight = flight;
        if (flight is { } tuning)
        {
            HoverHeight = MathUtils.Lerp(tuning.HoverMinBlocks, tuning.HoverMaxBlocks, Random.Shared.NextSingle());
            _bobPhase = Random.Shared.NextSingle() * MathF.Tau;
        }

        Animation = new EntityAnimator(type.Model.Animations, walkReferenceSpeed,
            flight == null ? EntityAnimator.WalkClip : EntityAnimator.FlyClip);
        if (spawnIn) Animation.Play(SpawnClip);
        else FinishSpawn();
    }

    /// <summary>The mob's own animation state, driven by its speed and actions.</summary>
    public EntityAnimator Animation { get; }

    /// <summary>How this kind flies, or null for a walker.</summary>
    public FlightTuning? Flight { get; }

    /// <inheritdoc />
    public override bool Flies => Flight.HasValue;

    /// <summary>A flyer's own hover height inside its kind's band, rolled at spawn so a flock spreads out
    /// (0 for a walker): over a chased target's eye (<see cref="FlightSteering.ChaseGoal"/>) or the floor.</summary>
    public float HoverHeight { get; }

    /// <summary>Height of the collision body; <see cref="Mob.Position"/> is its center.</summary>
    public abstract float BodyHeight { get; }

    /// <summary>Half the collision body's horizontal edge.</summary>
    public abstract float BodyHalfWidth { get; }

    /// <summary>Whether the mob currently stands on the ground.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Where the rig roots: the body center less half its height.</summary>
    public Vector3 Feet => Position - new Vector3(0f, BodyHeight * 0.5f, 0f);

    /// <inheritdoc />
    public override float BodyHalfExtent => BodyHalfWidth;

    /// <inheritdoc />
    public override float BodyHalfHeight => BodyHeight * 0.5f;

    /// <inheritdoc />
    public override float HeadAboveCenter => BodyHeight * 0.5f;

    /// <inheritdoc />
    public override Vector3 Velocity => _velocity;

    /// <summary>The item whose sprite color tints the armor shell drawn over rig part <paramref name="part"/>, or 0 for
    /// a bare part: none by default; a tower skeleton wears its outfit (<see cref="TowerMobs.SkeletonOutfit"/>).</summary>
    public virtual byte ArmorPieceOn(string part) => 0;

    /// <inheritdoc />
    protected override void SetVelocity(Vector3 velocity) => _velocity = velocity;

    /// <summary>Unit horizontal direction the mob faces (the model faces -Z at yaw 0).</summary>
    protected Vector3 Forward => new(-MathF.Sin(Facing), 0f, -MathF.Cos(Facing));

    // Unit horizontal direction to the mob's right
    private Vector3 Right => new(MathF.Cos(Facing), 0f, -MathF.Sin(Facing));

    private Vector3 HalfExtents => new(BodyHalfWidth, BodyHeight * 0.5f, BodyHalfWidth);

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        var wasDying = IsDying;
        base.TakeDamage(amount);
        // A flinch on top of whatever is playing, so a hit never cancels an attack.
        if (amount > 0 && !wasDying) Animation.PlayOverlay(HurtClip);
    }

    /// <summary>A client's mob is driven by server snapshots, not by its update: this advances the
    /// clips from the mirrored velocity so it still walks (or flies, by its 3D speed) and idles, and a
    /// flyer keeps pitching and banking with it.</summary>
    public override void AnimateMirror(float deltaTime)
    {
        base.AnimateMirror(deltaTime);
        Animation.Update(deltaTime, LocomotionSpeed);
        if (Flies) LeanIntoFlight(deltaTime);
        PlayDeathOnce();
    }

    // What the locomotion crossfade runs on: the horizontal speed of a walker, the 3D speed of a flyer
    private float LocomotionSpeed =>
        Flies ? _velocity.Length() : MathF.Sqrt(_velocity.X * _velocity.X + _velocity.Z * _velocity.Z);

    /// <summary>
    /// The pose the clips cannot give: a forward lean into the walk (backward when backpedaling) or a
    /// flyer's climb pitch and bank, a flinch recoil while the hit spring rings, the fall backward and
    /// shrink of the death, and the spawn pop; about the feet for a walker, the body center for a flyer.
    /// Multiply it after the part matrix (row-vector order).
    /// </summary>
    public Matrix4x4 BodyEffect(Vector3 feet)
    {
        var scale = MathUtils.EaseOutBack(SpawnProgress)
                    * (1f - MathUtils.EaseInCubic(MathUtils.InverseLerp(0.7f, 1f, DeathProgress)));
        var tilt = _lean + HitKick * HitLeanPerKick
                   + DeathFallRadians * MathUtils.EaseInCubic(MathUtils.InverseLerp(0f, 0.55f, DeathProgress));
        var pivot = Flies ? feet + new Vector3(0f, BodyHeight * 0.5f, 0f) : feet;
        // Positive about the right axis tips the head backward; the lean is stored as forward-positive.
        var effect = Matrix4x4.CreateTranslation(-pivot)
                     * Matrix4x4.CreateScale(MathF.Max(scale, 0.001f))
                     * Matrix4x4.CreateFromAxisAngle(Right, -tilt);
        // Positive about the forward axis dips the right side
        if (_bank != 0f) effect *= Matrix4x4.CreateFromAxisAngle(Forward, _bank);
        return effect * Matrix4x4.CreateTranslation(pivot);
    }

    /// <inheritdoc />
    public override bool IsMidAttack => !IsDying && Animation.IsActionPlaying && !Animation.IsPlaying(SpawnClip);

    /// <inheritdoc />
    public override void Shove(Vector3 direction, float speed)
    {
        if (!Flies) direction.Y = 0f;
        if (direction.LengthSquared() < 1e-6f) return;
        direction = Vector3.Normalize(direction);
        if (Flies)
        {
            _velocity = direction * speed;
            return;
        }
        _velocity.X = direction.X * speed;
        _velocity.Z = direction.Z * speed;
    }

    /// <summary>The start of a frame of the mob's own update: the life-cycle clocks, the hit flash
    /// fade and, once dying, the death clip (played once).</summary>
    protected void TickBody(float deltaTime)
    {
        TickLife(deltaTime);
        _hitFlash = MathF.Max(0f, _hitFlash - deltaTime / HitFlashSeconds);
        PlayDeathOnce();
    }

    private void PlayDeathOnce()
    {
        if (!IsDying || _deathPlayed) return;
        _deathPlayed = true;
        Animation.Play(DeathClip);
    }

    /// <summary>Turns the facing toward <paramref name="yaw"/> over a few frames, or at once on the
    /// very first look.</summary>
    protected void FaceYaw(float yaw, float deltaTime)
    {
        if (_facingKnown) TurnToward(yaw, TurnRate, deltaTime);
        else Facing = yaw;
        _facingKnown = true;
    }

    /// <summary>The yaw that points the model's -Z front along the horizontal <paramref name="to"/>.</summary>
    protected static float YawOf(Vector3 to) => MathF.Atan2(-to.X, -to.Z);

    /// <summary>
    /// A stroll toward <paramref name="goal"/> (a wander point) at <see cref="MobConfig.MobWanderSpeedFactor"/>:
    /// the move to hand to <see cref="Walk"/>, zero once within <see cref="MobConfig.MobWanderArriveBlocks"/>
    /// or while an action plays.
    /// </summary>
    protected Vector3 WanderMove(Vector3 goal, float deltaTime)
    {
        if (IsDying || Animation.IsActionPlaying) return Vector3.Zero;
        var to = new Vector3(goal.X - Position.X, 0f, goal.Z - Position.Z);
        var dist = to.Length();
        if (dist <= MobConfig.MobWanderArriveBlocks) return Vector3.Zero;
        FaceYaw(YawOf(to), deltaTime);
        return to / dist * MobConfig.MobWanderSpeedFactor;
    }

    /// <summary>
    /// Moves the body for one frame: never off a ledge the fall would punish (unless
    /// <paramref name="targetEye"/> is down there), the horizontal velocity eased toward
    /// <paramref name="move"/> times <paramref name="groundSpeed"/> on the ground or
    /// <paramref name="swimSpeed"/> afloat (friction toward a stop with no move), an auto-jump at
    /// <paramref name="jumpSpeed"/> over a step walked into last frame, then the physics, the
    /// clips and the lean.
    /// </summary>
    protected void Walk(Vector3 move, Vector3? targetEye, float groundSpeed, float swimSpeed, float jumpSpeed,
        float deltaTime, ChunkManager chunks)
    {
        // Ledge caution: never step off a drop the fall would punish unless the target is down there
        if (move != Vector3.Zero && IsGrounded
            && IsLedgeAhead(move, BodyHalfWidth + MobConfig.MobLedgeProbeBlocks, chunks, targetEye))
            move = Vector3.Zero;

        if (IsGrounded || _inWater)
        {
            var speed = IsGrounded ? groundSpeed : swimSpeed;
            var moving = move != Vector3.Zero;
            var rate = moving ? MoveAccel : IsGrounded ? BodyMotion.GroundFriction : 0f;
            if (rate > 0f)
            {
                _velocity.X = MathUtils.Damp(_velocity.X, move.X * speed, rate, deltaTime);
                _velocity.Z = MathUtils.Damp(_velocity.Z, move.Z * speed, rate, deltaTime);
            }
            if (!moving && IsGrounded)
            {
                if (MathF.Abs(_velocity.X) < 0.01f) _velocity.X = 0f;
                if (MathF.Abs(_velocity.Z) < 0.01f) _velocity.Z = 0f;
            }

            // Auto-jump a one-block step the mob walked into last frame.
            if (moving && _hitWall)
            {
                _velocity.Y = jumpSpeed;
                IsGrounded = false;
            }
        }

        ApplyPhysics(deltaTime, chunks);

        Animation.Update(deltaTime, LocomotionSpeed);

        // Lean into the movement along the facing; afloat it leans back a little as it paddles.
        var along = _velocity.X * Forward.X + _velocity.Z * Forward.Z;
        var leanTarget = Math.Clamp(along * LeanPerSpeed, -MaxLean, MaxLean) - (_inWater ? WaterLean : 0f);
        _lean = MathUtils.Damp(_lean, IsDying ? 0f : leanTarget, 8f, deltaTime);
    }

    /// <summary>
    /// One frame of flight, for a kind built with a <see cref="FlightTuning"/>: no gravity, ground
    /// friction, auto-jump, ledge caution or fall damage. The velocity is steered on all three axes
    /// toward <paramref name="goal"/> (<see cref="FlightSteering"/>: a chased target's
    /// <see cref="FlightSteering.ChaseGoal"/>, a wander point, or null to hover where it is at
    /// <see cref="HoverHeight"/> over the floor), its height kept by the floor rule (the hover band
    /// while idle, the minimum clearance while <paramref name="chasing"/>) plus a gentle bob, slid along
    /// walls by the probe, at <paramref name="speedFactor"/> of the tuning. The body then sweeps through
    /// <see cref="BoxSweep"/> like any mob, so it never enters a solid cell. It faces its goal, plays the
    /// fly clip by its 3D speed and pitches and banks. A dying flyer drops out of the sky.
    /// </summary>
    protected void Fly(Vector3? goal, bool chasing, float speedFactor, float deltaTime, ChunkManager chunks)
    {
        if (Flight is not { } flight)
            throw new InvalidOperationException($"Entity '{Type.Name}' has no flight tuning and cannot fly.");

        _flightClock += deltaTime;
        if (IsDying)
        {
            _velocity.Y = BodyMotion.Fall(_velocity.Y, deltaTime);
            if (IsGrounded) BodyMotion.ScrubOnGround(ref _velocity, deltaTime);
        }
        else
        {
            var half = HalfExtents;
            var bottom = Position.Y - half.Y;
            float? groundTop = FlightSteering.GroundBelow(chunks, Position, bottom,
                MobConfig.MobFlightGroundProbeBlocks) is { } depth ? bottom - depth : null;
            var aim = goal ?? Position with { Y = groundTop is { } floor ? floor + half.Y + HoverHeight : Position.Y };
            aim.Y = FlightSteering.GoalHeight(aim.Y + FlightSteering.BobOffset(_flightClock, _bobPhase, flight),
                groundTop, half.Y, flight, overGround: !chasing);

            var desired = FlightSteering.DesiredVelocity(Position, aim, flight, speedFactor);
            desired = FlightSteering.AvoidSolids(Position, half, desired, flight.VerticalSpeed * speedFactor, chunks);
            _velocity = BodyMotion.Steer(_velocity, desired, flight.Acceleration, deltaTime);

            var toGoal = new Vector3(aim.X - Position.X, 0f, aim.Z - Position.Z);
            if (toGoal.LengthSquared() > MobConfig.MobFlightArriveBlocks * MobConfig.MobFlightArriveBlocks)
                FaceYaw(YawOf(toGoal), deltaTime);
        }

        SweepBody(deltaTime, chunks);
        Animation.Update(deltaTime, LocomotionSpeed);
        LeanIntoFlight(deltaTime);
    }

    // A flyer's pitch (nose up in a climb, forward into its speed) and bank (into the turn)
    private void LeanIntoFlight(float deltaTime)
    {
        var along = Vector3.Dot(_velocity, Forward);
        var side = Vector3.Dot(_velocity, Right);
        var pitch = Math.Clamp(along * LeanPerSpeed - _velocity.Y * ClimbPitchPerSpeed, -MaxFlightPitch, MaxFlightPitch);
        var bank = Math.Clamp(-side * BankPerSpeed, -MaxBank, MaxBank);
        _lean = MathUtils.Damp(_lean, IsDying ? 0f : pitch, FlightLeanRate, deltaTime);
        _bank = MathUtils.Damp(_bank, IsDying ? 0f : bank, FlightLeanRate, deltaTime);
    }

    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        var feetY = Position.Y - BodyHeight * 0.5f;
        var feetCell = new Vector3(Position.X, feetY, Position.Z).BlockCell();
        var surfaceY = chunks.GetFluidSurfaceGlobal(feetCell.X, feetCell.Y, feetCell.Z);
        var floating = feetY < surfaceY;
        _inWater = floating;

        // Buoyancy, the slime's and the item drops' numbers: a floating body eases toward a rise
        // proportional to how far under the surface its feet are, so it climbs and bobs at the top.
        if (floating)
        {
            _velocity.Y = BodyMotion.FloatTowardSurface(_velocity.Y, surfaceY, feetY, deltaTime);
            BodyMotion.DragInWater(ref _velocity, deltaTime);
        }
        else
        {
            _velocity.Y = BodyMotion.Fall(_velocity.Y, deltaTime);
        }

        SweepBody(deltaTime, chunks);
    }

    // Moves the body by its velocity one axis at a time (X, Z, then Y), stopping each at a solid
    private void SweepBody(float deltaTime, ChunkManager chunks)
    {
        var movement = _velocity * deltaTime;
        var half = HalfExtents;

        Position = BoxSweep.MoveOnAxis(Position, movement.X, 0, half, chunks, out var hitX);
        Position = BoxSweep.MoveOnAxis(Position, movement.Z, 2, half, chunks, out var hitZ);
        Position = BoxSweep.MoveOnAxis(Position, movement.Y, 1, half, chunks, out var hitY);

        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;
        _hitWall = hitX || hitZ;

        if (hitY)
        {
            // IsGrounded still holds last frame's answer here, so this is the landing frame
            if (movement.Y < 0f && !IsGrounded) NoteLanding(-_velocity.Y, BodyMotion.Gravity, _inWater);
            IsGrounded = movement.Y < 0f;
            _velocity.Y = 0f;
        }
        else
        {
            IsGrounded = false;
        }
    }
}
