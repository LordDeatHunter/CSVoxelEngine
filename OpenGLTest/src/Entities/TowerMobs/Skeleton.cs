using System;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Goblins;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The skeleton: the battle tower's undead, an archer or a warrior (<see cref="Role"/>, by its entity kind). In melee
/// reach and off its cooldown either strikes its target (the melee clip; the blow lands at <see cref="ImpactEvent"/>,
/// <see cref="TryTakeMeleeImpact"/>): the warrior stabs hard with its copper dagger, the archer only punches. The
/// warrior otherwise walks in. The archer keeps its distance: inside
/// <see cref="MobConfig.SkeletonBowMinRange"/>..<see cref="MobConfig.SkeletonBowMaxRange"/>, standing on the ground
/// off its cooldown with a clear line to the chest, it draws its bow (the shoot clip; the arrow leaves at
/// <see cref="ReleaseEvent"/>, <see cref="TryTakeShotRelease"/>); inside the band without a line it walks around the
/// target to a spot that has one (<see cref="VantageSeeker"/>), nearer than the band it backs away, farther it walks
/// in. Its damage (its health stays the fixed pool of its kind) is scaled by the tower floor it was raised on (<see cref="Tier"/>,
/// <see cref="TowerDifficulty"/>), and its <see cref="Outfit"/> softens every blow it takes; the tier and the outfit are set at spawn,
/// since a mirror cannot derive them from where it stands, and ride its wire byte (<see cref="PackLook"/>). Two attack
/// clips share the replicated attack count: an even count is a strike, an odd one a shot. Main thread only.
/// </summary>
public sealed class Skeleton : RealmMob
{
    /// <summary>The archer's entity file name (<c>assets/data/entities/skeleton.json</c>).</summary>
    public const string EntityName = "skeleton";

    /// <summary>The warrior's entity file name (<c>assets/data/entities/skeleton_warrior.json</c>).</summary>
    public const string WarriorEntityName = "skeleton_warrior";

    // The wire byte: the tier in the low bits, the role above it, the outfit code on top
    private const int TierBits = 3;
    private const int RoleShift = TierBits;
    private const int OutfitShift = TierBits + 1;

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
    private Vector3 _aimVelocity;
    private readonly VantageSeeker _vantage = new();

    /// <param name="type">The skeleton kind, with its model and clips.</param>
    /// <param name="position">The body center it spawns at.</param>
    /// <param name="tier">The tower floor tier it was raised on, clamped to the floors that exist.</param>
    /// <param name="spawnIn">Plays the spawn clip and the pop-in; false starts settled.</param>
    /// <param name="role">How it fights, which must match the kind of <paramref name="type"/>.</param>
    /// <param name="outfit">The armor it wears.</param>
    public Skeleton(EntityType type, Vector3 position, int tier, bool spawnIn = true,
        SkeletonRole role = SkeletonRole.Archer, SkeletonOutfit outfit = default)
        : base(type, position, MobConfig.SkeletonWalkSpeed, spawnIn)
    {
        Tier = TowerDifficulty.Clamp(tier);
        Role = role;
        Outfit = outfit;
        Health = MaxHealth;
    }

    /// <summary>The tower floor tier this skeleton was raised on, fixed for life.</summary>
    public int Tier { get; }

    /// <summary>How it fights, fixed for life.</summary>
    public SkeletonRole Role { get; }

    /// <summary>The armor it wears, fixed for life.</summary>
    public SkeletonOutfit Outfit { get; }

    /// <summary>The role an entity kind name stands for: the warrior's kind, else the archer.</summary>
    public static SkeletonRole RoleOf(string kind) =>
        string.Equals(kind, WarriorEntityName, StringComparison.OrdinalIgnoreCase) ? SkeletonRole.Warrior : SkeletonRole.Archer;

    /// <summary>The entity kind name of <paramref name="role"/>.</summary>
    public static string EntityNameOf(SkeletonRole role) => role == SkeletonRole.Warrior ? WarriorEntityName : EntityName;

    /// <summary>The wire byte of a skeleton (<c>NetEntity.ItemId</c>): its tier, role and outfit code.</summary>
    public static byte PackLook(int tier, SkeletonRole role, SkeletonOutfit outfit) =>
        (byte)(TowerDifficulty.Clamp(tier) | (int)role << RoleShift | outfit.Code << OutfitShift);

    /// <summary>The tier, role and outfit <see cref="PackLook"/> packed into <paramref name="look"/>, the tier clamped
    /// to the floors that exist.</summary>
    public static void UnpackLook(byte look, out int tier, out SkeletonRole role, out SkeletonOutfit outfit)
    {
        tier = TowerDifficulty.Clamp(look & ((1 << TierBits) - 1));
        role = (SkeletonRole)((look >> RoleShift) & 1);
        outfit = SkeletonOutfit.FromCode(look >> OutfitShift);
    }

    /// <summary>
    /// The tower floor whose lock this skeleton counts toward, set by the bone pile that raised it or by a save
    /// restore; null for a scenario's or a client mirror's skeleton. Saved in <c>entities.json</c>, never sent on
    /// the wire.
    /// </summary>
    public TowerFloorRef? Floor { get; set; }

    /// <summary>
    /// Whether this skeleton crumbles back into its floor's bone piles: it is alive, bound to a floor
    /// (<see cref="Floor"/>) and has chased nobody (no target noticed, or its sight of one lost, <see cref="MobInterest"/>)
    /// for <see cref="MobConfig.SkeletonPileReturnIdleSeconds"/>. An unbound skeleton has no pile and never does.
    /// </summary>
    public bool ReturnsToPile =>
        Floor != null && !IsDying && !IsDead && Interest.IdleSeconds >= MobConfig.SkeletonPileReturnIdleSeconds;

    /// <inheritdoc />
    public override bool IsUndead => true;

    /// <inheritdoc />
    public override string HitSoundGroup => SfxLibrary.SkeletonHitGroup;

    /// <inheritdoc />
    public override string DeathSoundGroup => SfxLibrary.SkeletonDeathGroup;

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

    /// <summary>The damage one melee blow deals on this skeleton's tier: the warrior's dagger stab, the archer's punch.</summary>
    public int MeleeDamage => TowerDifficulty.Scale(
        Role == SkeletonRole.Warrior ? MobConfig.SkeletonMeleeDamage : MobConfig.SkeletonArcherPunchDamage, Tier);

    /// <summary>How hard a melee blow shoves: the dagger's full knockback, a punch's lighter one.</summary>
    public float MeleeKnockbackSpeed => Role == SkeletonRole.Warrior
        ? MobConfig.SkeletonMeleeKnockbackSpeed
        : MobConfig.SkeletonArcherPunchKnockbackSpeed;

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

    /// <summary>A blow loses the outfit's share (<see cref="SkeletonOutfit.DamageReduction"/>), never below 1.</summary>
    public override int ScaleDamage(int amount, Vector3 from) =>
        amount <= 0 ? amount : Math.Max(1, (int)MathF.Round(amount * (1f - Outfit.DamageReduction)));

    /// <inheritdoc />
    public override ushort ArmorPieceOn(string part) => Outfit.TintItemOn(part);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _meleeCooldown = MathF.Max(0f, _meleeCooldown - scaledDelta);
        _shotCooldown = MathF.Max(0f, _shotCooldown - scaledDelta);
        _vantage.Tick(scaledDelta);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InMeleeReach(Position, target.Eye))
        {
            if (_meleeCooldown > 0f) return Vector3.Zero;
            StartAttack(MeleeVariant);
            _meleeCooldown = Role == SkeletonRole.Warrior
                ? MobConfig.SkeletonMeleeCooldownSeconds
                : MobConfig.SkeletonArcherPunchCooldownSeconds;
            return Vector3.Zero;
        }

        // A warrior has no bow: it closes in. An archer nearer than its band backs away to shoot again (a target that
        // keeps up is punched), and one farther than it walks in.
        if (Role == SkeletonRole.Warrior) return toward;
        if (dist < MobConfig.SkeletonBowMinRange || dist > MobConfig.SkeletonBowMaxRange)
        {
            _vantage.Clear();
            return dist < MobConfig.SkeletonBowMinRange ? -toward : toward;
        }

        // Inside the band without a line: walk around the target to a spot that has one (walking at it only pushes
        // into the wall), or at it when no spot is standable at all.
        var chest = ChestOf(target.Eye);
        if (!SeesTarget(ShotOrigin, target, chunks))
            return _vantage.Move(Position, ShotOrigin, BodyHeight, chest, MobConfig.SkeletonBowMinRange,
                MobConfig.SkeletonBowMaxRange, chunks) ?? toward;

        // With a line: shoot when grounded (so a jump never lifts the bow over a wall) and off the cooldown
        _vantage.Clear();
        if (!IsGrounded || _shotCooldown > 0f) return Vector3.Zero;

        _aimTarget = chest;
        _aimVelocity = target.Velocity;
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
        GoblinAim.TryLeadVelocity(origin, _aimTarget, _aimVelocity, MobConfig.SkeletonArrowSpeed,
            ThrownProjectile.Gravity, MobConfig.RangedWindupLeadSeconds, out velocity);
        return true;
    }
}
