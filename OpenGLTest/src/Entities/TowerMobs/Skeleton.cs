using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Goblins;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The skeleton: the battle tower's undead archer. In melee reach and off its cooldown it rakes its target
/// (the melee clip; the blow lands at <see cref="ImpactEvent"/>, <see cref="TryTakeMeleeImpact"/>); inside
/// <see cref="MobConfig.SkeletonBowMinRange"/>..<see cref="MobConfig.SkeletonBowMaxRange"/>, standing on the ground
/// off its cooldown with a clear line to the chest, it draws its bow (the shoot clip; the arrow leaves at
/// <see cref="ReleaseEvent"/>, <see cref="TryTakeShotRelease"/>); otherwise it walks in. Its health and damage are
/// scaled by the tower floor it was raised on (<see cref="Tier"/>, <see cref="TowerDifficulty"/>), set at spawn
/// since a mirror cannot derive it from where it stands. Two attack clips share the replicated attack count: an
/// even count is a rake, an odd one a shot. Main thread only.
/// </summary>
public sealed class Skeleton : RealmMob
{
    /// <summary>The entity file name (<c>assets/data/entities/skeleton.json</c>).</summary>
    public const string EntityName = "skeleton";

    /// <summary>The melee clip, attack variant <see cref="MeleeVariant"/>.</summary>
    public const string MeleeClip = "melee";

    /// <summary>Timeline event on the melee clip at which the rake connects.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>The bow clip, attack variant <see cref="ShotVariant"/>.</summary>
    public const string ShootClip = "shoot";

    /// <summary>Timeline event on the shoot clip at which the arrow leaves the bow.</summary>
    public const string ReleaseEvent = "release";

    /// <summary>The attack variant of the rake.</summary>
    public const int MeleeVariant = 0;

    /// <summary>The attack variant of the bow shot.</summary>
    public const int ShotVariant = 1;

    private float _meleeCooldown;
    private float _shotCooldown = MobConfig.SkeletonFirstShotDelaySeconds;
    private Vector3 _aimTarget;

    /// <param name="type">The skeleton kind, with its model and clips.</param>
    /// <param name="position">The body center it spawns at.</param>
    /// <param name="tier">The tower floor tier it was raised on, clamped to the floors that exist.</param>
    /// <param name="spawnIn">Plays the spawn clip and the pop-in; false starts settled.</param>
    public Skeleton(EntityType type, Vector3 position, int tier, bool spawnIn = true)
        : base(type, position, MobConfig.SkeletonWalkSpeed, spawnIn)
    {
        Tier = TowerDifficulty.Clamp(tier);
        Health = MaxHealth;
    }

    /// <summary>The tower floor tier this skeleton was raised on, fixed for life.</summary>
    public int Tier { get; }

    /// <summary>
    /// The tower floor whose lock this skeleton counts toward, set by the bone pile that raised it or by a save
    /// restore; null for a scenario's or a client mirror's skeleton. Saved in <c>entities.json</c>, never sent on
    /// the wire.
    /// </summary>
    public TowerFloorRef? Floor { get; set; }

    /// <inheritdoc />
    public override int MaxHealth => TowerDifficulty.Scale(Type.MaxHealth, Tier);

    /// <inheritdoc />
    public override bool IsUndead => true;

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.SkeletonHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.SkeletonHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.SkeletonWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.SkeletonJumpSpeed;

    /// <inheritdoc />
    public override string AttackClip => MeleeClip;

    /// <inheritdoc />
    protected override int AttackVariants => 2;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant == ShotVariant ? ShootClip : MeleeClip;

    /// <summary>The damage one rake deals on this skeleton's tier.</summary>
    public int MeleeDamage => TowerDifficulty.Scale(MobConfig.SkeletonMeleeDamage, Tier);

    /// <summary>The damage one arrow deals on this skeleton's tier.</summary>
    public int ArrowDamage => TowerDifficulty.Scale(MobConfig.SkeletonArrowDamage, Tier);

    /// <summary>Whether a skeleton centered at <paramref name="position"/> can land its rake on a player whose eye
    /// is at <paramref name="eye"/>.</summary>
    public static bool InMeleeReach(Vector3 position, Vector3 eye) =>
        MobTargets.InMeleeReach(position, eye, MobConfig.SkeletonMeleeReach);

    /// <summary>Where an arrow leaves the bow: a little in front of the body at shoulder height.</summary>
    public Vector3 ShotOrigin => Position + Forward * 0.4f + new Vector3(0f, BodyHeight * 0.3f, 0f);

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk, a melee clip
    /// carrying the impact event and a shoot clip carrying the release event.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireHumanoid(type, "Skeleton", MeleeClip, ImpactEvent);
        EntityContract.RequireEvent(type, "Skeleton", EntityContract.RequireClip(type, "Skeleton", ShootClip),
            ReleaseEvent);
    }

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _meleeCooldown = MathF.Max(0f, _meleeCooldown - scaledDelta);
        _shotCooldown = MathF.Max(0f, _shotCooldown - scaledDelta);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InMeleeReach(Position, target.Eye))
        {
            if (_meleeCooldown > 0f) return Vector3.Zero;
            StartAttack(MeleeVariant);
            _meleeCooldown = MobConfig.SkeletonMeleeCooldownSeconds;
            return Vector3.Zero;
        }

        // Too far to shoot, or too close: walk in (from inside the band it closes to melee)
        if (dist > MobConfig.SkeletonBowMaxRange || dist < MobConfig.SkeletonBowMinRange) return toward;

        // Inside the band: shoot when grounded (so a jump never lifts the bow over a wall), off the cooldown
        // and with a clear line; otherwise keep walking at the target until the line clears.
        if (!SeesTarget(ShotOrigin, target, chunks)) return toward;
        if (!IsGrounded || _shotCooldown > 0f) return Vector3.Zero;

        _aimTarget = ChestOf(target.Eye);
        StartAttack(ShotVariant);
        var jitter = (Random.Shared.NextSingle() - 0.5f) * 2f * MobConfig.SkeletonShotCooldownJitter;
        _shotCooldown = MobConfig.SkeletonShotCooldownSeconds + jitter;
        return Vector3.Zero;
    }

    /// <summary>True once per rake, at the melee clip's impact event; the manager decides whether the target is
    /// still in reach.</summary>
    public bool TryTakeMeleeImpact() => Animation.ConsumeEvent(ImpactEvent);

    /// <summary>
    /// True once per shot, at the shoot clip's release event: where the arrow leaves the bow and the launch
    /// velocity that carries it onto the chest the shot was aimed at (GoblinAim's low arc, a maximum-range toss
    /// toward it when out of reach).
    /// </summary>
    public bool TryTakeShotRelease(out Vector3 origin, out Vector3 velocity)
    {
        origin = default;
        velocity = default;
        if (!Animation.ConsumeEvent(ReleaseEvent)) return false;

        origin = ShotOrigin;
        GoblinAim.TryLaunchVelocity(origin, _aimTarget, MobConfig.SkeletonArrowSpeed, ThrownProjectile.Gravity,
            out velocity);
        return true;
    }
}
