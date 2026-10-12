using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A goblin: a short forest raider that lobs the flint it carries at the player from a
/// standoff band, backing off when the player closes in, and once its stones run out runs in
/// for a club swing. It walks rather than hops (auto-jumping a one-block step), floats in water
/// like the slime, and is animated from the Blockbench clips in its model through an
/// <see cref="EntityAnimator"/>: the stone leaves the hand at the throw clip's
/// <see cref="ReleaseEvent"/> and the swing lands at the melee clip's <see cref="ImpactEvent"/>,
/// which <c>GoblinManager</c> collects through <see cref="TryTakeThrowRelease"/> and
/// <see cref="TryTakeMeleeImpact"/>. Main thread only.
/// </summary>
public sealed class Goblin : Mob
{
    /// <summary>Groups the goblin's model must provide; the renderer draws each one.</summary>
    public static readonly string[] RequiredParts = ["Torso", "Head", "ArmL", "ArmR", "LegL", "LegR"];

    /// <summary>The part that holds and throws the stone.</summary>
    public const string HandPart = "ArmR";

    public const string ThrowClip = "throw";
    public const string MeleeClip = "melee";

    // Optional clips: absent ones simply play nothing, the procedural BodyEffect still shows.
    public const string HurtClip = "hurt";
    public const string DeathClip = "death";
    public const string SpawnClip = "spawn";

    /// <summary>Timeline event on the throw clip at which the stone leaves the hand.</summary>
    public const string ReleaseEvent = "release";

    /// <summary>Timeline event on the melee clip at which the swing connects.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>Draw scale of the flint held in the throwing hand while ammo remains.</summary>
    public const float HeldStoneScale = 0.28f;

    /// <summary>Scale the off hand's carried torch is drawn at (the block's model, not an item
    /// sprite).</summary>
    public const float HeldTorchScale = 0.5f;

    /// <summary>The bone holding a carried torch: the off hand, where the flint rides the right.</summary>
    public const string OffHandPart = "ArmL";

    // The rigs the animation poses, one per entity type and shared with nothing GL-bound, so the
    // torch flame point can be read without the renderer
    private static readonly ConcurrentDictionary<EntityType, PlayerRig> RigCache = new();

    /// <summary>Half the collision body's horizontal edge.</summary>
    public const float HalfExtent = 0.3f;

    /// <summary>Height of the collision body; <see cref="Mob.Position"/> is its centre, so the
    /// feet are at <c>Position.Y - Height / 2</c>.</summary>
    public const float Height = 1.3f;

    private const float Gravity = 28f;
    private const float TerminalVelocity = -30f;
    private const float CollisionStepSize = 0.05f;
    private const float GroundFriction = 8f;

    // Damp rate of the walking velocity toward the chosen move, so a goblin speeds up and turns
    // over a few frames instead of snapping to full speed.
    private const float MoveAccel = 12f;

    // Buoyancy, the slime's numbers: a floating goblin eases toward a rise proportional to how
    // far under the surface its feet are, so it climbs and bobs at the top.
    private const float FloatSpeed = 1.5f;
    private const float BuoyancyStiffness = 3f;
    private const float WaterDrag = 4f;
    private const float WaterFriction = 3f;

    // A floating goblin paddles toward its goal at this fraction of its walk speed, so it can
    // cross a pond and climb out instead of bobbing in place forever.
    private const float SwimSpeedFactor = 0.5f;

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

    private Vector3 _velocity;
    private bool _inWater;
    private float _lean;
    private bool _facingKnown;
    private bool _deathPlayed;
    private float _throwCooldown;
    private float _meleeCooldown;
    private float _losRetry;
    private bool _losBlocked;
    private Vector3 _aimTarget;
    private bool _hitWall;
    // Which way the goblin sidesteps a charged throw aimed at it: fixed per goblin at spawn from
    // its cell, so the same goblin always breaks the same way instead of jittering
    private readonly float _dodgeSide;
    // Whether this goblin has ever started a throw; the pack alert only pulls a first throw forward
    private bool _thrownOnce;
    // Whether the manager last read the light as dark (a carried torch is out then)
    private bool _dark;
    // Set on the frame the goblin first noticed a target, read (and cleared) by the manager, which
    // alerts the pack with it
    private bool _justNoticed;
    private float _noticePipElapsed = WorldGenerationConfig.GoblinNoticePipSeconds;
    private bool _noticed;

    public Goblin(EntityType type, Vector3 position, int ammo, bool spawnIn = true, int torches = 0) : base(type, position)
    {
        Ammo = Math.Max(0, ammo);
        Torches = Math.Clamp(torches, 0, WorldGenerationConfig.GoblinTorchMaxCount);
        Animation = new EntityAnimator(type.Model.Animations, WorldGenerationConfig.GoblinWalkSpeed);
        // The dodge side: fixed at spawn from the starting cell, so the same goblin always breaks
        // the same way out of a charged throw's line
        _dodgeSide = ((MathUtils.FloorToInt(position.X) + MathUtils.FloorToInt(position.Z)) & 1) == 0 ? 1f : -1f;
        // A fresh goblin notices the player before its first throw rather than throwing on its
        // first grounded frame; unjittered so scripted shots repeat.
        _throwCooldown = WorldGenerationConfig.GoblinFirstThrowDelaySeconds;
        if (spawnIn) Animation.Play(SpawnClip);
        else FinishSpawn();
    }

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        var wasDying = IsDying;
        base.TakeDamage(amount);
        // A flinch on top of whatever is playing, so a hit never cancels a throw or swing.
        if (amount > 0 && !wasDying) Animation.PlayOverlay(HurtClip);
    }

    /// <summary>A client's goblin is driven by server snapshots, not by <see cref="Update"/>: this
    /// advances the clips from the mirrored velocity so it still walks and idles.</summary>
    public override void AnimateMirror(float deltaTime)
    {
        base.AnimateMirror(deltaTime);
        Animation.Update(deltaTime, MathF.Sqrt(_velocity.X * _velocity.X + _velocity.Z * _velocity.Z));
        if (IsDying && !_deathPlayed)
        {
            _deathPlayed = true;
            Animation.Play(DeathClip);
        }
    }

    /// <summary>Stones left to throw; the manager drops what remains on death.</summary>
    public int Ammo { get; private set; }

    /// <summary>Adds scavenged stones (flint picked up off the ground), clamped at
    /// <see cref="WorldGenerationConfig.GoblinStoneMax"/>.</summary>
    public void AddAmmo(int amount) =>
        Ammo = Math.Clamp(Ammo + amount, 0, WorldGenerationConfig.GoblinStoneMax);

    /// <summary>Torches this goblin still carries; it equips one while it is dark and they drop on
    /// death.</summary>
    public int Torches { get; private set; }

    /// <summary>Whether a carried torch is out: one is left and the manager last read it as dark
    /// (<see cref="SetDark"/>).</summary>
    public bool TorchLit => Torches > 0 && _dark;

    /// <summary>The manager's per-frame light reading for this goblin: whether a carried torch
    /// would be out (<see cref="GoblinTorches.Lit"/>).</summary>
    public void SetDark(float night, bool underground) => _dark = GoblinTorches.Lit(1, night, underground);

    /// <summary>
    /// Where a lit torch's flame burns in the world (the off hand's anchor through the body
    /// effect, the same matrices the renderer draws the torch with), or null while none is out.
    /// The world's fire gathering reads it, so a torch-bearing goblin lights its area and sheds
    /// embers.
    /// </summary>
    public Vector3? TorchFlamePoint
    {
        get
        {
            if (!TorchLit) return null;
            var feet = Position - new Vector3(0f, BodyHalfHeight, 0f);
            var rig = RigCache.GetOrAdd(Type, static type => PlayerRig.FromBbmodel(type.Model));
            var anchor = Animation.HandAnchor(OffHandPart, rig, feet, Facing) * BodyEffect(feet);
            return anchor.Translation;
        }
    }

    /// <summary>Whether the goblin currently stands on the ground.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>
    /// Whether the goblin has given up: at or below <see cref="WorldGenerationConfig.GoblinFleeHealthShare"/>
    /// of its pool it turns and runs from the player instead of fighting, faster than its walk
    /// (<see cref="WorldGenerationConfig.GoblinFleeSpeedFactor"/>); it still despawns by distance
    /// like any goblin.
    /// </summary>
    public bool IsFleeing => Health > 0 && Health <= MaxHealth * WorldGenerationConfig.GoblinFleeHealthShare;

    /// <summary>
    /// Takes whether this goblin first noticed a target since the last call (true once): the
    /// manager uses it to alert the pack (goblins within
    /// <see cref="WorldGenerationConfig.GoblinPackAlertRadiusBlocks"/> skip their first-throw delay).
    /// </summary>
    public bool TakeJustNoticed()
    {
        var noticed = _justNoticed;
        _justNoticed = false;
        return noticed;
    }

    /// <summary>
    /// Another goblin of the pack just noticed a player: skip this goblin's first-throw delay so
    /// the pack joins in together. Only a goblin that has not thrown yet (and has stones) is
    /// pulled forward; a later alert never resets a running cooldown.
    /// </summary>
    public void AlertPack()
    {
        if (_thrownOnce || IsDying || Ammo <= 0) return;
        _throwCooldown = 0f;
    }

    /// <summary>
    /// The notice pip's clock: 0 on the frame the goblin first had a player to chase (the same
    /// moment its first-throw delay starts), rising to 1 over
    /// <see cref="WorldGenerationConfig.GoblinNoticePipSeconds"/>; 1 for a goblin that never
    /// noticed anyone, and never restarted when a target is lost and regained.
    /// </summary>
    public float NoticePipProgress =>
        MathUtils.Clamp01(_noticePipElapsed / WorldGenerationConfig.GoblinNoticePipSeconds);

    /// <summary>The goblin's own animation state, driven by its speed and actions.</summary>
    public EntityAnimator Animation { get; }

    /// <inheritdoc />
    public override float DeathSeconds => 0.8f;

    /// <summary>
    /// The pose the clips cannot give: about the feet, a forward lean into the walk (backward when
    /// backpedalling), a flinch recoil while the hit spring rings, the fall backward and shrink of
    /// the death, and the spawn pop. Multiply it after the part matrix (row-vector order).
    /// </summary>
    public Matrix4x4 BodyEffect(Vector3 feet)
    {
        var scale = MathUtils.EaseOutBack(SpawnProgress)
                    * (1f - MathUtils.EaseInCubic(MathUtils.InverseLerp(0.7f, 1f, DeathProgress)));
        var tilt = _lean + HitKick * HitLeanPerKick
                   + DeathFallRadians * MathUtils.EaseInCubic(MathUtils.InverseLerp(0f, 0.55f, DeathProgress));
        var right = new Vector3(MathF.Cos(Facing), 0f, -MathF.Sin(Facing));
        // Positive about the right axis tips the head backward; the lean is stored as forward-positive.
        return Matrix4x4.CreateTranslation(-feet)
               * Matrix4x4.CreateScale(MathF.Max(scale, 0.001f))
               * Matrix4x4.CreateFromAxisAngle(right, -tilt)
               * Matrix4x4.CreateTranslation(feet);
    }

    /// <inheritdoc />
    public override Vector3 Velocity => _velocity;

    /// <inheritdoc />
    protected override void SetVelocity(Vector3 velocity) => _velocity = velocity;

    /// <inheritdoc />
    public override float BodyHalfExtent => HalfExtent;

    /// <inheritdoc />
    public override float BodyHalfHeight => Height * 0.5f;

    /// <inheritdoc />
    public override float HeadAboveCentre => Height * 0.5f;

    /// <summary>Unit horizontal direction the goblin faces (the model faces -Z at yaw 0).</summary>
    private Vector3 Forward => new(-MathF.Sin(Facing), 0f, -MathF.Cos(Facing));

    /// <summary>Where a thrown stone leaves the hand: a little in front of the body at
    /// shoulder height.</summary>
    public Vector3 ThrowOrigin => Position + Forward * 0.4f + new Vector3(0f, 0.35f, 0f);

    /// <summary>Where a stone is aimed on a player whose eye is at <paramref name="eye"/>: the
    /// chest, a little under the eye.</summary>
    public static Vector3 ChestOf(Vector3 eye) => eye - new Vector3(0f, 0.4f, 0f);

    /// <summary>
    /// Whether a goblin body centred at <paramref name="goblinPosition"/> can land a swing on a
    /// player whose eye is at <paramref name="playerEye"/>: horizontally within
    /// <see cref="WorldGenerationConfig.GoblinMeleeReach"/> and roughly level with the player's
    /// body centre.
    /// </summary>
    public static bool InMeleeReach(Vector3 goblinPosition, Vector3 playerEye)
    {
        var dx = playerEye.X - goblinPosition.X;
        var dz = playerEye.Z - goblinPosition.Z;
        var reach = WorldGenerationConfig.GoblinMeleeReach;
        if (dx * dx + dz * dz > reach * reach) return false;
        return MathF.Abs(goblinPosition.Y - (playerEye.Y - Camera.EyeHeight * 0.5f)) <= 1.5f;
    }

    /// <summary>
    /// Throws, naming the model file, unless the goblin's model provides every
    /// <see cref="RequiredParts"/> group, the idle, walk, throw and melee clips, and the
    /// release and impact events the throw and swing are timed by.
    /// </summary>
    public static void ValidateContent(EntityType type)
    {
        var model = type.Model;
        foreach (var part in RequiredParts)
        {
            if (!model.Parts.TryGetValue(part, out var group) || group.Cubes.Count == 0)
                throw new InvalidOperationException(
                    $"Goblin model '{type.ModelPath}' needs a non-empty group named '{part}'.");
        }

        RequireClip(type, EntityAnimator.IdleClip);
        RequireClip(type, EntityAnimator.WalkClip);
        RequireEvent(type, RequireClip(type, ThrowClip), ReleaseEvent);
        RequireEvent(type, RequireClip(type, MeleeClip), ImpactEvent);
    }

    private static AnimationClip RequireClip(EntityType type, string name)
    {
        foreach (var clip in type.Model.Animations)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase))
                return clip;
        }

        throw new InvalidOperationException(
            $"Goblin model '{type.ModelPath}' has no animation named '{name}'.");
    }

    private static void RequireEvent(EntityType type, AnimationClip clip, string eventName)
    {
        foreach (var e in clip.Events)
        {
            if (string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException(
            $"Goblin model '{type.ModelPath}' animation '{clip.Name}' has no '{eventName}' timeline event.");
    }

    /// <inheritdoc />
    public override void Shove(Vector3 horizontalDirection, float speed)
    {
        if (horizontalDirection.LengthSquared() < 1e-6f) return;
        horizontalDirection.Y = 0f;
        horizontalDirection = Vector3.Normalize(horizontalDirection);
        _velocity.X = horizontalDirection.X * speed;
        _velocity.Z = horizontalDirection.Z * speed;
    }

    /// <summary>
    /// Advances the AI, the physics and the animation. With stones left the goblin keeps to the
    /// throw band (<see cref="WorldGenerationConfig.GoblinThrowMinRange"/>..
    /// <see cref="WorldGenerationConfig.GoblinThrowMaxRange"/>) and starts a throw whenever it
    /// stands on the ground with a clear line to the player's chest; out of stones it closes to
    /// the standoff distance and starts a swing when in reach. It stands still while a throw or
    /// swing plays. A null <paramref name="player"/> (nobody to chase) leaves the goblin standing
    /// where it is, still falling, floating and animating, or strolling toward
    /// <paramref name="wander"/> when one is given (<see cref="Mob.WanderGoal"/>).
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, IMobTarget? player, Vector3? wander = null,
        bool dodgeAimedThrow = false)
    {
        if (deltaTime <= 0f || chunks == null) return;

        TickLife(deltaTime);
        _hitFlash = MathF.Max(0f, _hitFlash - deltaTime / HitFlashSeconds);
        _throwCooldown = MathF.Max(0f, _throwCooldown - deltaTime);
        _meleeCooldown = MathF.Max(0f, _meleeCooldown - deltaTime);
        _losRetry = MathF.Max(0f, _losRetry - deltaTime);
        _noticePipElapsed = MathF.Min(WorldGenerationConfig.GoblinNoticePipSeconds,
            _noticePipElapsed + deltaTime);

        var move = Vector3.Zero;
        if (IsDying)
        {
            player = null;
            if (!_deathPlayed)
            {
                _deathPlayed = true;
                Animation.Play(DeathClip);
            }
        }
        if (player != null)
        {
            // The first sight of a player starts the pip; it never restarts on a re-notice
            if (!_noticed)
            {
                _noticed = true;
                _noticePipElapsed = 0f;
                _justNoticed = true;
            }

            var target = player.Eye;
            var to = new Vector3(target.X - Position.X, 0f, target.Z - Position.Z);
            var dist = to.Length();
            var toward = dist > 1e-3f ? to / dist : Vector3.Zero;
            // The model faces -Z at yaw 0, so this rotation points -Z at the player. It turns over a
            // few frames rather than snapping, except on the very first look.
            if (dist > 1e-3f)
            {
                var yaw = MathF.Atan2(-to.X, -to.Z);
                // A fleeing goblin faces down its escape, not at the player it runs from
                if (IsFleeing) yaw += MathF.PI;
                if (_facingKnown) TurnToward(yaw, TurnRate, deltaTime);
                else Facing = yaw;
                _facingKnown = true;
            }

            move = Decide(dist, toward, chunks, player, dodgeAimedThrow);
        }
        else if (wander is { } goal && !IsDying && !Animation.IsActionPlaying)
        {
            // Nobody chased (none noticed, or interest lost): stroll toward the wander goal
            var to = new Vector3(goal.X - Position.X, 0f, goal.Z - Position.Z);
            var dist = to.Length();
            if (dist > WorldGenerationConfig.MobWanderArriveBlocks)
            {
                TurnToward(MathF.Atan2(-to.X, -to.Z), TurnRate, deltaTime);
                _facingKnown = true;
                move = to / dist * WorldGenerationConfig.MobWanderSpeedFactor;
            }
        }

        // Ledge caution: never step off a drop the fall would punish unless the target is down there
        if (move != Vector3.Zero && IsGrounded
            && IsLedgeAhead(move, HalfExtent + WorldGenerationConfig.MobLedgeProbeBlocks, chunks, player?.Eye))
            move = Vector3.Zero;

        // Walking: ease the horizontal velocity toward the chosen move (or friction toward a
        // stop), on the ground at full speed and afloat at a paddle.
        if (IsGrounded || _inWater)
        {
            var speed = IsGrounded
                ? WorldGenerationConfig.GoblinWalkSpeed
                  * (IsFleeing ? WorldGenerationConfig.GoblinFleeSpeedFactor : 1f)
                : WorldGenerationConfig.GoblinWalkSpeed * SwimSpeedFactor;
            var moving = move != Vector3.Zero;
            var rate = moving ? MoveAccel : IsGrounded ? GroundFriction : 0f;
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

            // Auto-jump a one-block step the goblin walked into last frame.
            if (moving && _hitWall)
            {
                _velocity.Y = WorldGenerationConfig.GoblinJumpSpeed;
                IsGrounded = false;
            }
        }

        ApplyPhysics(deltaTime, chunks);

        var planarSpeed = MathF.Sqrt(_velocity.X * _velocity.X + _velocity.Z * _velocity.Z);
        Animation.Update(deltaTime, planarSpeed);

        // Lean into the movement along the facing; afloat it leans back a little as it paddles.
        var along = _velocity.X * Forward.X + _velocity.Z * Forward.Z;
        var leanTarget = Math.Clamp(along * LeanPerSpeed, -MaxLean, MaxLean) - (_inWater ? WaterLean : 0f);
        _lean = MathUtils.Damp(_lean, IsDying ? 0f : leanTarget, 8f, deltaTime);
    }

    // Chooses this frame's horizontal move (a unit vector or zero) and starts a throw or swing
    // when one is due.
    private Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget player,
        bool dodgeAimedThrow)
    {
        if (Animation.IsActionPlaying) return Vector3.Zero;

        // A charged throw aimed at it: sidestep out of the line (the side fixed per goblin so it
        // never jitters), never throw or swing while the aim holds
        if (dodgeAimedThrow) return new Vector3(-toward.Z, 0f, toward.X) * _dodgeSide;

        // Given up: turn and run, never throw or swing
        if (IsFleeing) return -toward;

        if (Ammo > 0)
        {
            if (dist > WorldGenerationConfig.GoblinThrowMaxRange) return toward;
            if (dist < WorldGenerationConfig.GoblinThrowMinRange) return -toward;

            // Grounded only, so the auto-jump can never lift the throw origin over a wall the
            // goblin is walking into; the LOS test is throttled while it stays blocked.
            if (IsGrounded && _throwCooldown <= 0f && _losRetry <= 0f)
            {
                var chest = ChestOf(player.Eye);
                if (!BlockRaycaster.SegmentHitsBlock(ThrowOrigin, chest, chunks))
                {
                    _losBlocked = false;
                    _aimTarget = chest;
                    _thrownOnce = true;
                    Animation.Play(ThrowClip);
                    _throwCooldown = WorldGenerationConfig.GoblinThrowCooldownSeconds
                                     + (Random.Shared.NextSingle() - 0.5f) * 2f * WorldGenerationConfig.GoblinThrowCooldownJitter;
                    return Vector3.Zero;
                }

                _losBlocked = true;
                _losRetry = WorldGenerationConfig.GoblinLosRetrySeconds;
            }

            // Inside the band: hold, unless the last look found the line blocked, in which case
            // keep walking at the player until a throw finds it clear again.
            return _losBlocked ? toward : Vector3.Zero;
        }

        if (InMeleeReach(Position, player.Eye) && _meleeCooldown <= 0f)
        {
            Animation.Play(MeleeClip);
            _meleeCooldown = WorldGenerationConfig.GoblinMeleeCooldownSeconds;
            return Vector3.Zero;
        }

        return dist > WorldGenerationConfig.GoblinStandoffBlocks ? toward : Vector3.Zero;
    }

    /// <summary>
    /// True once per throw, at the throw clip's release event while stones remain: spends one
    /// stone and returns where it leaves the hand and the launch velocity that lobs it onto the
    /// chest the throw was aimed at (a maximum-range toss toward it when out of reach).
    /// </summary>
    public bool TryTakeThrowRelease(out Vector3 origin, out Vector3 velocity)
    {
        origin = default;
        velocity = default;
        if (!Animation.ConsumeEvent(ReleaseEvent) || Ammo <= 0) return false;

        origin = ThrowOrigin;
        // The bool is ignored: an out-of-range player still gets the fallback toss.
        GoblinAim.TryLaunchVelocity(origin, _aimTarget, WorldGenerationConfig.GoblinStoneSpeed,
            ThrownProjectile.Gravity, out velocity);
        Ammo--;
        return true;
    }

    /// <summary>True once per swing, at the melee clip's impact event; the manager decides
    /// whether the player is still in reach.</summary>
    public bool TryTakeMeleeImpact() => Animation.ConsumeEvent(ImpactEvent);

    private void ApplyPhysics(float deltaTime, ChunkManager chunks)
    {
        var feetY = Position.Y - Height * 0.5f;
        var feetCell = new Vector3(Position.X, feetY, Position.Z).BlockCell();
        var surfaceY = chunks.GetFluidSurfaceGlobal(feetCell.X, feetCell.Y, feetCell.Z);
        var floating = feetY < surfaceY;
        _inWater = floating;

        if (floating)
        {
            var targetY = Math.Clamp((surfaceY - feetY) * BuoyancyStiffness, -FloatSpeed, FloatSpeed);
            _velocity.Y = MathUtils.Damp(_velocity.Y, targetY, WaterDrag, deltaTime);

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

        if (hitX) _velocity.X = 0f;
        if (hitZ) _velocity.Z = 0f;
        _hitWall = hitX || hitZ;

        if (hitY)
        {
            // IsGrounded still holds last frame's answer here, so this is the landing frame
            if (movement.Y < 0f && !IsGrounded) NoteLanding(-_velocity.Y, Gravity, _inWater);
            IsGrounded = movement.Y < 0f;
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
        var half = new Vector3(HalfExtent, Height * 0.5f, HalfExtent);
        VoxelCoords.CellRange(center - half, center + half,
            out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ);

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
