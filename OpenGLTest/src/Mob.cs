using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// A living hostile entity: the state every mob kind shares (its <see cref="EntityType"/>,
/// body centre, health, facing and hit flash) plus the body dimensions the targeting, projectile
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
        Type = type;
        Position = position;
        Health = type.MaxHealth;
    }

    public EntityType Type { get; }

    /// <summary>Body centre.</summary>
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

    /// <summary>A springy 0-centred recoil the last hit started (positive = pushed in); the renderer
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

    /// <summary>Skips the spawn pop: the mob counts as already settled (a debugger restart of a mob that is not
    /// being shown spawning).</summary>
    public void FinishSpawn() => _spawnTime = SpawnInSeconds;

    /// <summary>Starts the death animation (idempotent). The owner rolls the loot at this moment.</summary>
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
    public virtual float SafeFallBlocks => WorldGenerationConfig.MobSafeFallBlocks;

    /// <summary>
    /// Records that the body just landed at <paramref name="impactSpeed"/> blocks per second under
    /// <paramref name="gravity"/>, for the owner's landing dust to take (<see cref="TryTakeLanding"/>).
    /// Two landings before a take keep the harder one; nothing but the cosmetic dust reads it. The
    /// landing also bills its fall (<see cref="MobFallDamage"/> over <see cref="SafeFallBlocks"/>)
    /// unless the feet were <paramref name="inWater"/>; only a mob's own physics lands, which runs in
    /// the authoritative simulation alone, so a client's mirror never takes fall damage.
    /// </summary>
    protected void NoteLanding(float impactSpeed, float gravity, bool inWater = false)
    {
        if (!inWater)
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

    /// <summary>Horizontal half extent of the collision body, used by touch and hit tests.</summary>
    public abstract float BodyHalfExtent { get; }

    /// <summary>Vertical half extent of the collision body about <see cref="Position"/>.</summary>
    public abstract float BodyHalfHeight { get; }

    /// <summary>Height of the top of the drawn body above <see cref="Position"/>, where the
    /// healthbar is placed.</summary>
    public abstract float HeadAboveCentre { get; }

    /// <summary>Counts the hits <see cref="TakeDamage"/> accepted, so a caller can tell whether its
    /// hit landed by comparing it before and after (the world shows each landed hit as a floating
    /// damage number of <see cref="LastHitDamage"/>).</summary>
    public int HitsTaken { get; private set; }

    /// <summary>The amount of the last hit <see cref="TakeDamage"/> accepted.</summary>
    public int LastHitDamage { get; private set; }

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

    /// <summary>Sets the horizontal shove a contact or hit imparts, leaving vertical motion
    /// alone. Setting (not adding) keeps a sustained touch from accelerating the mob.</summary>
    public abstract void Shove(Vector3 horizontalDirection, float speed);

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
