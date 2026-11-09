using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Particles;
using VoxelEngine.Status;

namespace VoxelEngine.Entities;

/// <summary>
/// A living hostile entity: the state every mob kind shares (its <see cref="EntityType"/>,
/// body center, health, facing and hit flash) plus the body dimensions the targeting, projectile
/// and healthbar code reads, so those paths serve every mob kind through one list instead of one
/// code path per kind. Main thread only.
/// </summary>
public abstract class Mob
{
    /// <summary>Hit flash amount, snapped to 1 by <see cref="TakeDamage"/>; each subclass fades it.</summary>
    protected float _hitFlash;

    /// <summary>How long a killed mob stays on screen playing its death before it is removed.</summary>
    public const float DefaultDeathSeconds = 0.5f;

    /// <summary>How long a fresh mob takes to pop in.</summary>
    public const float SpawnInSeconds = 0.35f;

    private float _deathTime;
    private float _spawnTime;
    private Spring _kick = new(3.5f, 0.3f);

    protected Mob(EntityType type, Vector3 position)
    {
        if (type.MaxHealth < 1)
            throw new InvalidOperationException($"Entity '{type.Name}' has no hp; a mob needs a health pool.");
        Type = type;
        Position = position;
        Health = type.MaxHealth;
    }

    public EntityType Type { get; }

    /// <summary>Whether this mob flies (<see cref="RiggedMob.Fly"/>): no gravity, no fall damage, no ledge
    /// caution, a 3D wander goal and a 3D knock. Fixed by its kind, so nothing about it rides the wire.</summary>
    public virtual bool Flies => false;

    /// <summary>Body center.</summary>
    public Vector3 Position { get; protected set; }

    public int Health { get; protected set; }

    /// <summary>This mob's full health: its kind's pool (<see cref="EntityType.MaxHealth"/>), or a
    /// subclass's own (a slime scales it by its size). A subclass that overrides it sets
    /// <see cref="Health"/> to it in its constructor, since the base constructor cannot.</summary>
    public virtual int MaxHealth => Type.MaxHealth;

    public bool IsDead => Health <= 0;

    /// <summary>Yaw in radians the body faces; the model faces -Z at 0.</summary>
    public float Facing { get; protected set; }

    /// <summary>Hit flash amount, 1 right after <see cref="TakeDamage"/> fading back to 0 along an
    /// ease-out (quick drop, soft tail). The entity pass tints the skin red by this (non-emissive).</summary>
    public float HitFlash => _hitFlash * _hitFlash;

    /// <summary>A springy 0-centered recoil the last hit started (positive = pushed in); the renderer
    /// squashes or leans the body by it.</summary>
    public float HitKick => _kick.Value;

    /// <summary>Whether the mob has been killed and is playing its death; it is no longer a target,
    /// a threat or part of the live count, but is still drawn until <see cref="IsRemovable"/>.</summary>
    public bool IsDying { get; private set; }

    /// <summary>Seconds the death plays before the owner removes the mob.</summary>
    public virtual float DeathSeconds => DefaultDeathSeconds;

    /// <summary>0 when the death starts, 1 when it is over (0 for a mob that is not dying).</summary>
    public float DeathProgress => IsDying ? MathUtils.Clamp01(_deathTime / DeathSeconds) : 0f;

    /// <summary>True once the death has played out and the owner may drop the mob.</summary>
    public bool IsRemovable => IsDying && _deathTime >= DeathSeconds;

    /// <summary>0 at spawn, 1 once the pop-in is over.</summary>
    public float SpawnProgress => MathUtils.Clamp01(_spawnTime / SpawnInSeconds);

    /// <summary>Seconds this mob has lived (its own simulated time since it spawned or was restored).</summary>
    public float AgeSeconds => _spawnTime;

    /// <summary>Skips the spawn pop: the mob counts as already settled (a debugger restart of a mob that is not
    /// being shown spawning).</summary>
    public void FinishSpawn() => _spawnTime = SpawnInSeconds;

    /// <summary>Starts the death animation (idempotent). The owner rolls the loot at this moment for a
    /// kill; a despawn by distance starts it too, with no loot, so the mob fades out instead of vanishing.</summary>
    public void BeginDying()
    {
        if (IsDying) return;
        IsDying = true;
        _deathTime = 0f;
    }

    /// <summary>Advances the life-cycle clocks and the hit spring; every subclass calls it once a frame.</summary>
    protected void TickLife(float deltaTime)
    {
        _spawnTime += deltaTime;
        if (IsDying) _deathTime += deltaTime;
        _kick.Step(0f, deltaTime);
    }

    /// <summary>Advances a mob whose AI a client never runs (death, spawn, hit recoil and flash).</summary>
    public virtual void AnimateMirror(float deltaTime)
    {
        TickLife(deltaTime);
        _hitFlash = MathF.Max(0f, _hitFlash - deltaTime / 0.25f);
    }

    /// <summary>Eases <see cref="Facing"/> toward a yaw (radians) instead of snapping to it.</summary>
    protected void TurnToward(float targetYaw, float rate, float deltaTime) =>
        Facing = MathUtils.DampAngle(Facing * (180f / MathF.PI), targetYaw * (180f / MathF.PI), rate, deltaTime)
                 * (MathF.PI / 180f);

    /// <summary>Gives the body a hit recoil; <paramref name="strength"/> is roughly blocks of squash per second.</summary>
    protected void KickBody(float strength) => _kick.Impulse(strength);

    private float _landingSpeed;
    private float _landingGravity;

    /// <summary>The drop in blocks this mob lands from unhurt (<see cref="MobFallDamage"/>); a slime
    /// adds its own hop's apex, so its hops never hurt it.</summary>
    public virtual float SafeFallBlocks => MobConfig.MobSafeFallBlocks;

    /// <summary>
    /// Records that the body just landed at <paramref name="impactSpeed"/> blocks per second under
    /// <paramref name="gravity"/>, for the owner's landing dust to take (<see cref="TryTakeLanding"/>).
    /// Two landings before a take keep the harder one; nothing but the cosmetic dust reads it. The
    /// landing also bills its fall (<see cref="MobFallDamage"/> over <see cref="SafeFallBlocks"/>)
    /// unless the feet were <paramref name="inWater"/>; only a mob's own physics lands, which runs in
    /// the authoritative simulation alone, so a client's mirror never takes fall damage. A flyer
    /// (<see cref="Flies"/>) never falls, so it never bills one either.
    /// </summary>
    protected void NoteLanding(float impactSpeed, float gravity, bool inWater = false)
    {
        if (!inWater && !Flies)
        {
            var damage = MobFallDamage.Damage(LandingDust.FallFromImpactSpeed(impactSpeed, gravity),
                SafeFallBlocks, MaxHealth);
            if (damage > 0) TakeDamage(damage);
        }

        if (impactSpeed <= _landingSpeed) return;
        _landingSpeed = impactSpeed;
        _landingGravity = gravity;
    }

    /// <summary>
    /// True once per landing noted since the last take: the speed the body hit the ground at and
    /// the height it fell to reach it. A world that spawns no dust (a headless server) never takes,
    /// which leaves one float standing.
    /// </summary>
    public bool TryTakeLanding(out float impactSpeed, out float fallBlocks)
    {
        impactSpeed = _landingSpeed;
        fallBlocks = LandingDust.FallFromImpactSpeed(_landingSpeed, _landingGravity);
        _landingSpeed = 0f;
        return impactSpeed > 0f;
    }

    private readonly MobInterest _interest = new();
    private Vector3 _wanderGoal;
    private float _wanderTimer;
    private bool _hasWanderGoal;

    /// <summary>Where the mob looks from for its line of sight: a little above the body center.</summary>
    public Vector3 SightOrigin => Position + new Vector3(0f, BodyHalfHeight * 0.6f, 0f);

    /// <summary>This mob's interest in what it noticed (lost after a while out of sight, idle time).</summary>
    public MobInterest Interest => _interest;

    /// <summary>
    /// The shared chase rule each manager asks once a frame before the mob's update: the target the
    /// notice rule picks (<see cref="MobTargets.Nearest"/>), unless this mob has lost interest in it
    /// after <see cref="MobConfig.MobLoseInterestSeconds"/> without a clear line of sight
    /// (<see cref="MobInterest"/>). The index into <paramref name="targets"/>, or -1 when it chases nobody.
    /// </summary>
    public int PickTarget(IReadOnlyList<IMobTarget> targets, float deltaTime, ChunkManager chunks)
    {
        var index = MobTargets.Nearest(targets, Position);
        var chases = _interest.Update(index >= 0 ? targets[index] : null, SightOrigin, deltaTime, chunks);
        if (!chases) return -1;
        _hasWanderGoal = false;
        return index;
    }

    /// <summary>
    /// Where a mob that chases nobody strolls to: a goal <see cref="MobInterest.WanderPoint"/> picks
    /// around it, picked anew every <see cref="MobConfig.MobWanderRepickSeconds"/> or once
    /// reached. Null when there is nobody in play at all (<paramref name="targetCount"/> 0), so an empty
    /// server's mobs stand still. A flyer's goal is 3D (<see cref="MobInterest.FlyingWanderPoint"/>, an air
    /// cell of <paramref name="chunks"/> when given) and is reached in 3D.
    /// </summary>
    public Vector3? WanderGoal(int targetCount, float deltaTime, ChunkManager? chunks = null)
    {
        if (targetCount <= 0 || IsDying) return null;
        _wanderTimer -= deltaTime;
        var to = _wanderGoal - Position;
        if (!Flies) to.Y = 0f;
        var arrive = MobConfig.MobWanderArriveBlocks;
        if (!_hasWanderGoal || _wanderTimer <= 0f || to.LengthSquared() < arrive * arrive)
        {
            _wanderGoal = Flies
                ? FlyingWanderGoal(chunks)
                : MobInterest.WanderPoint(Position, Random.Shared.NextSingle(), Random.Shared.NextSingle());
            _wanderTimer = MobConfig.MobWanderRepickSeconds * (0.75f + 0.5f * Random.Shared.NextSingle());
            _hasWanderGoal = true;
        }
        return _wanderGoal;
    }

    // A 3D wander point in an air cell, rerolled a few times; hovering in place when every roll hits a block
    private Vector3 FlyingWanderGoal(ChunkManager? chunks)
    {
        for (var i = 0; i < MobConfig.MobFlightWanderTries; i++)
        {
            var point = MobInterest.FlyingWanderPoint(Position, Random.Shared.NextSingle(),
                Random.Shared.NextSingle(), Random.Shared.NextSingle());
            var cell = point.BlockCell();
            if (chunks == null || chunks.GetBlockGlobal(cell.X, cell.Y, cell.Z) == 0)
                return point;
        }
        return Position;
    }

    /// <summary>Whether this mob, idle long enough and far from every one of <paramref name="targets"/>,
    /// despawns (<see cref="MobInterest.ShouldIdleDespawn"/>); never with nobody in play.</summary>
    public bool ShouldIdleDespawn(IReadOnlyList<IMobTarget> targets) =>
        targets.Count > 0
        && MobInterest.ShouldIdleDespawn(_interest.IdleSeconds, MobTargets.MinHorizontalDistanceSquared(targets, Position));

    /// <summary>
    /// Ledge caution (<see cref="LedgeProbe"/>): whether moving along the horizontal
    /// <paramref name="direction"/> to <paramref name="distance"/> blocks from the body center would step
    /// off a drop of more than <see cref="MobConfig.MobLedgeMaxDropBlocks"/>, with
    /// <paramref name="targetEye"/> (the chased target's eye, or null) not standing below it. Never for a
    /// flyer, which has no ledge to fall off.
    /// </summary>
    public bool IsLedgeAhead(Vector3 direction, float distance, ChunkManager chunks, Vector3? targetEye) =>
        !Flies
        && LedgeProbe.IsLedgeAhead(chunks, Position, Position.Y - BodyHalfHeight, direction, distance, targetEye);

    private float _lavaTimer;

    /// <summary>The status effects on this mob (<see cref="StatusEffects"/>): ticked by
    /// <see cref="UpdateHazards"/> in the authoritative simulation, mirrored from the server on a client.</summary>
    public StatusEffectSet Effects { get; } = new();

    /// <summary>The movement speed factor of the active status effects (1 when none slows).</summary>
    public float MoveFactor => Effects.MoveFactor;

    /// <summary>The attack pace factor of the active status effects (1 when none slows).</summary>
    public float AttackFactor => Effects.AttackFactor;

    /// <summary>
    /// The shared world hazards, called by the owning manager once a frame right after the mob's own
    /// update, in the authoritative simulation only. The status effects tick first (their damage over
    /// time lands through <see cref="TakeDamage"/>; a dying mob's effects are cleared). Then a body box
    /// in lava (<see cref="MobLava"/>) burns one damage tick per <see cref="MobConfig.MobLavaTickSeconds"/>
    /// with the hit flash, and leaving it (or dying) clears the partial tick. A death either causes rolls
    /// the loot as usual, since the manager checks <see cref="IsDead"/> after this.
    /// </summary>
    public void UpdateHazards(float deltaTime, ChunkManager chunks)
    {
        if (deltaTime <= 0f || chunks == null) return;
        UpdateEffects(deltaTime);
        UpdateLava(deltaTime, chunks);
    }

    private void UpdateEffects(float deltaTime)
    {
        if (IsDying)
        {
            Effects.Clear();
            return;
        }

        var damage = Effects.Tick(deltaTime, MaxHealth);
        if (damage > 0) TakeDamage(damage);
    }

    private void UpdateLava(float deltaTime, ChunkManager chunks)
    {
        if (IsDying || !MobLava.BodyInLava(Position, BodyHalfExtent, BodyHalfHeight, chunks))
        {
            _lavaTimer = 0f;
            return;
        }

        _lavaTimer += deltaTime;
        while (_lavaTimer >= MobConfig.MobLavaTickSeconds && !IsDead)
        {
            _lavaTimer -= MobConfig.MobLavaTickSeconds;
            TakeDamage(MobLava.DamagePerTick(MaxHealth));
        }
    }

    /// <summary>The mob's water-splash memory, stepped by the world from its feet each frame
    /// (<see cref="WaterSplash.Track"/>); cosmetic, so a server never steps it.</summary>
    public WaterSplashTracker Splash { get; } = new();

    /// <summary>Horizontal half extent of the collision body, used by touch and hit tests.</summary>
    public abstract float BodyHalfExtent { get; }

    /// <summary>Vertical half extent of the collision body about <see cref="Position"/>.</summary>
    public abstract float BodyHalfHeight { get; }

    /// <summary>Height of the top of the drawn body above <see cref="Position"/>, where the
    /// healthbar is placed.</summary>
    public abstract float HeadAboveCenter { get; }

    /// <summary>Counts the hits <see cref="TakeDamage"/> accepted, so a caller can tell whether its
    /// hit landed by comparing it before and after (the world shows each landed hit as a floating
    /// damage number of <see cref="LastHitDamage"/>).</summary>
    public int HitsTaken { get; private set; }

    /// <summary>The amount of the last hit <see cref="TakeDamage"/> accepted.</summary>
    public int LastHitDamage { get; private set; }

    /// <summary>
    /// Whether a cube of half extent <paramref name="halfExtent"/> centered at <paramref name="center"/> touches
    /// this mob's body (with a small slack): the body box by default; a multi-part body (the sandworm) tests
    /// every exposed part. Shared by thrown items and spell bolts (<see cref="ThrownProjectile.FindMobHit"/>).
    /// </summary>
    public virtual bool OverlapsBox(Vector3 center, float halfExtent)
    {
        var d = center - Position;
        var reachXz = halfExtent + BodyHalfExtent + 0.05f;
        var reachY = halfExtent + BodyHalfHeight + 0.05f;
        return MathF.Abs(d.X) <= reachXz && MathF.Abs(d.Z) <= reachXz && MathF.Abs(d.Y) <= reachY;
    }

    /// <summary>
    /// Slides the body horizontally by <paramref name="delta"/> through the shared block sweep, so a
    /// nudge can never push it into a solid cell: how <see cref="MobSeparation"/> resolves two
    /// bodies standing in the same space.
    /// </summary>
    internal void Nudge(Vector3 delta, ChunkManager chunks)
    {
        var half = new Vector3(BodyHalfExtent, BodyHalfHeight, BodyHalfExtent);
        if (delta.X != 0f) Position = BoxSweep.MoveOnAxis(Position, delta.X, 0, half, chunks, out _);
        if (delta.Z != 0f) Position = BoxSweep.MoveOnAxis(Position, delta.Z, 2, half, chunks, out _);
    }

    /// <summary>
    /// The point melee swings aim at and test their line of sight to, seen from <paramref name="from"/>: the body
    /// center by default; a multi-part body gives its nearest exposed part, or null while none can be hit (a worm
    /// fully under the sand), which keeps the mob out of every swing.
    /// </summary>
    public virtual Vector3? AimPointFrom(Vector3 from) => Position;

    /// <summary>The damage a blow of <paramref name="amount"/> from <paramref name="from"/> deals: unchanged by
    /// default; a multi-part body scales it by the part the blow lands on.</summary>
    public virtual int ScaleDamage(int amount, Vector3 from) => amount;

    /// <summary>Whether the floating healthbar is drawn over this mob: always, except a boss hidden under the
    /// ground (the sandworm), whose bar would hang in the sand.</summary>
    public virtual bool ShowsHealthbar => true;

    /// <summary>Whether this mob is a boss: while one lives (and is not dying) the HUD shows its boss bar.</summary>
    public virtual bool IsBoss => false;

    /// <summary>How far from the viewer this boss's bar still shows, in blocks: unlimited by default (the sandworm);
    /// a boss bound to an arena (the Skeleton King) shows its bar only nearby.</summary>
    public virtual float BossBarRange => float.PositiveInfinity;

    /// <summary>Whether this mob is undead (the tower's skeletons): gear with undead damage hits it harder.</summary>
    public virtual bool IsUndead => false;

    /// <summary>Sound group (see <c>SfxLibrary</c>) played when a hit lands on this mob without killing it; null for silence.</summary>
    public virtual string? HitSoundGroup => null;

    /// <summary>Sound group played when a hit kills this mob; null for silence.</summary>
    public virtual string? DeathSoundGroup => null;

    /// <summary>Applies a hit; the owner drops the loot and removes the mob once dead.</summary>
    public virtual void TakeDamage(int amount)
    {
        if (amount <= 0 || IsDying) return;
        Health = Math.Max(0, Health - amount);
        _hitFlash = 1f;
        KickBody(4f);
        HitsTaken++;
        LastHitDamage = amount;
    }

    /// <summary>Sets the shove a contact or hit imparts along <paramref name="direction"/>: a ground mob
    /// takes only its horizontal part and keeps its vertical motion, a flyer (<see cref="Flies"/>) is
    /// knocked along all three axes. Setting (not adding) keeps a sustained touch from accelerating the mob.</summary>
    public abstract void Shove(Vector3 direction, float speed);

    /// <summary>Current body velocity in blocks per second (what an entity save records).</summary>
    public abstract Vector3 Velocity { get; }

    /// <summary>Replaces the body velocity; the save-restore path of <see cref="RestoreState"/>.</summary>
    protected abstract void SetVelocity(Vector3 velocity);

    /// <summary>
    /// Puts back the health, facing and velocity a save recorded, on a mob freshly spawned at
    /// its saved position. The health is clamped into 1..<see cref="MaxHealth"/>, so
    /// a restored mob is never dead on arrival and never above its pool (a retuned
    /// <c>hp</c> wins). AI timers and the animation stay the constructor's: they restart.
    /// </summary>
    internal void RestoreState(int health, float facing, Vector3 velocity)
    {
        Health = Math.Clamp(health, 1, Math.Max(1, MaxHealth));
        Facing = facing;
        SetVelocity(velocity);
    }

    /// <summary>
    /// Adopts a server broadcast on a client: the authoritative position, facing, health and
    /// velocity, without the spawn clamps <see cref="RestoreState"/> applies (a client may see a
    /// mob at 0 health for the frame before the death broadcast removes it).
    /// </summary>
    internal void NetworkSetState(Vector3 position, float facing, int health, Vector3 velocity)
    {
        Position = position;
        Facing = facing;
        Health = Math.Clamp(health, 0, Math.Max(1, MaxHealth));
        SetVelocity(velocity);
    }
}
