using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The boar, and its oversized cousin the dire boar: neutral until provoked. A boar roots about and
/// ignores everyone until it is hurt, or a player stands within
/// <see cref="WildlifeConfig.BoarCrowdRangeBlocks"/> of it for <see cref="WildlifeConfig.BoarCrowdSeconds"/>
/// (it stops and stares meanwhile). Provoked, it paws the ground once (its <c>threat</c> clip), then charges
/// in its run and gores at the <see cref="ImpactEvent"/> of its <c>gore</c> clip, which the manager bills to
/// whoever is still in reach (<see cref="TryTakeStrike"/>, <see cref="InGoreReach"/>). Its anger outlasts
/// its last sight of the target by <see cref="WildlifeConfig.BoarAngerSeconds"/>. The dire boar is provoked
/// by anyone it notices within <see cref="WildlifeConfig.DireBoarNoticeRangeBlocks"/>. Main thread only.
/// </summary>
public sealed class Boar : Animal
{
    /// <summary>The entity file names (<c>assets/data/entities/boar.json</c>, <c>dire_boar.json</c>).</summary>
    public const string EntityName = "boar";
    public const string DireEntityName = "dire_boar";

    public const string GoreClip = "gore";
    public const string ThreatClip = "threat";
    public const string RootClip = "root";
    public const string ChargeClip = "run";

    /// <summary>Timeline event on the gore clip at which the tusks strike.</summary>
    public const string ImpactEvent = "impact";

    // The counted action clips (RealmMob.StartAttack), which a client's mirror replays by this index
    private const int GoreAction = 0;
    private const int ThreatAction = 1;
    private const int RootAction = 2;

    private float _anger;
    private float _crowdedSeconds;
    private bool _crowdedNow;
    private bool _threatened;
    private float _goreCooldown;
    private float _rootIn = Roll(WildlifeConfig.BoarRootMinSeconds, WildlifeConfig.BoarRootMaxSeconds);

    public Boar(EntityType type, Vector3 position, bool dire, bool spawnIn = true)
        : base(type, position, dire ? WildlifeConfig.DireBoarWalkSpeed : WildlifeConfig.BoarWalkSpeed, spawnIn)
    {
        IsDire = dire;
    }

    /// <summary>Whether this is the dire boar.</summary>
    public bool IsDire { get; }

    /// <summary>Whether the boar is angry: it charges whoever it reacts to.</summary>
    public bool IsProvoked => _anger > 0f;

    // The dire boar is a fight worth reading: its bar always shows, an ordinary boar's once it is hurt or angry
    /// <inheritdoc />
    public override bool ShowsHealthbar => IsDire || IsProvoked || base.ShowsHealthbar;

    /// <inheritdoc />
    public override WildlifeKind Kind => IsDire ? WildlifeKind.DireBoar : WildlifeKind.Boar;

    /// <inheritdoc />
    public override string? HitSoundGroup => IsDire ? SfxLibrary.DireBoarHitGroup : SfxLibrary.BoarHitGroup;

    /// <inheritdoc />
    public override string? DeathSoundGroup => IsDire ? SfxLibrary.DireBoarDeathGroup : SfxLibrary.BoarDeathGroup;

    /// <inheritdoc />
    public override float BodyHeight => IsDire ? WildlifeConfig.DireBoarHeight : WildlifeConfig.BoarHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => IsDire ? WildlifeConfig.DireBoarHalfExtent : WildlifeConfig.BoarHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => IsDire ? WildlifeConfig.DireBoarShoveSpeed : WildlifeConfig.BoarShoveSpeed;

    /// <inheritdoc />
    protected override float WalkSpeed => IsDire ? WildlifeConfig.DireBoarWalkSpeed : WildlifeConfig.BoarWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => WildlifeConfig.BoarJumpSpeed;

    /// <inheritdoc />
    protected override float GroundSpeedFactor => IsProvoked && _threatened ? RunSpeed / WalkSpeed : 1f;

    /// <inheritdoc />
    protected override string RunClip => ChargeClip;

    /// <inheritdoc />
    protected override float RunSpeed => IsDire ? WildlifeConfig.DireBoarChargeSpeed : WildlifeConfig.BoarChargeSpeed;

    /// <inheritdoc />
    public override string AttackClip => GoreClip;

    /// <inheritdoc />
    protected override int AttackVariants => 3;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant switch
    {
        ThreatAction => ThreatClip,
        RootAction => RootClip,
        _ => GoreClip,
    };

    // Only its rooting is a habit it drops; the threat and the gore play out
    /// <inheritdoc />
    protected override bool IdleActionPlaying => Animation.IsPlaying(RootClip);

    /// <inheritdoc />
    protected override bool ActsThroughAction => Animation.IsPlaying(RootClip);

    /// <summary>How far the tusks reach from the body center.</summary>
    public float GoreReach => IsDire ? WildlifeConfig.DireBoarGoreReach : WildlifeConfig.BoarGoreReach;

    /// <inheritdoc />
    public override int StrikeDamage => IsDire ? WildlifeConfig.DireBoarGoreDamage : WildlifeConfig.BoarGoreDamage;

    /// <inheritdoc />
    public override float StrikeKnockbackSpeed =>
        IsDire ? WildlifeConfig.DireBoarKnockbackSpeed : WildlifeConfig.BoarKnockbackSpeed;

    /// <inheritdoc />
    public override float StrikeKnockbackLift =>
        IsDire ? WildlifeConfig.DireBoarKnockbackLift : WildlifeConfig.BoarKnockbackLift;

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) => InGoreReach(Position, eye, GoreReach);

    /// <summary>Whether a boar centered at <paramref name="center"/> with the given <paramref name="reach"/>
    /// can gore a player whose eye is at <paramref name="eye"/>.</summary>
    public static bool InGoreReach(Vector3 center, Vector3 eye, float reach) =>
        MobTargets.InMeleeReach(center, eye, reach);

    /// <summary>Throws, naming the model file, unless the model is a four-legged rig on the humanoid group
    /// names with idle, walk, run, root and threat clips and a gore clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireHumanoid(type, "Boar", GoreClip, ImpactEvent);
        foreach (var clip in (string[])[ChargeClip, RootClip, ThreatClip])
            EntityContract.RequireClip(type, "Boar", clip);
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets)
    {
        if (IsProvoked) return true;
        var range = IsDire ? WildlifeConfig.DireBoarNoticeRangeBlocks : WildlifeConfig.BoarCrowdRangeBlocks;
        return NearestThreatSquared(targets) <= range * range;
    }

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        base.TakeDamage(amount);
        if (amount > 0) Provoke();
    }

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _goreCooldown = MathF.Max(0f, _goreCooldown - scaledDelta);
        _anger = MathF.Max(0f, _anger - scaledDelta);

        // A player standing close for long enough provokes it; stepping back out resets the count
        _crowdedSeconds = _crowdedNow ? _crowdedSeconds + scaledDelta : 0f;
        _crowdedNow = false;
        if (_crowdedSeconds >= WildlifeConfig.BoarCrowdSeconds) Provoke();
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!IsProvoked)
        {
            if (!IsDire)
            {
                // Crowded: it stops rooting and stares until the player backs off or its patience ends
                _crowdedNow = true;
                Animation.CutAction();
                return Vector3.Zero;
            }
            Provoke();
        }

        if (!_threatened)
        {
            _threatened = true;
            Animation.CutAction();
            StartAttack(ThreatAction);
            return Vector3.Zero;
        }

        // Its anger holds for as long as it can see whom it is after
        if (SeesTarget(EyePoint, target, chunks)) _anger = WildlifeConfig.BoarAngerSeconds;
        if (!InGoreReach(Position, target.Eye, GoreReach)) return toward;
        if (_goreCooldown > 0f) return Vector3.Zero;

        StartAttack(GoreAction);
        _goreCooldown = WildlifeConfig.BoarGoreCooldownSeconds;
        return Vector3.Zero;
    }

    /// <inheritdoc />
    protected override void IdleTick(float deltaTime)
    {
        if (IsProvoked) return;
        _rootIn -= deltaTime;
        if (_rootIn > 0f || !IsGrounded || Animation.IsActionPlaying) return;
        _rootIn = Roll(WildlifeConfig.BoarRootMinSeconds, WildlifeConfig.BoarRootMaxSeconds);
        StartAttack(RootAction);
    }

    /// <inheritdoc />
    public override bool TryTakeStrike() => Animation.ConsumeEvent(ImpactEvent);

    // Makes it angry; a boar that was calm owes its threat display again
    private void Provoke()
    {
        if (!IsProvoked) _threatened = false;
        _anger = WildlifeConfig.BoarAngerSeconds;
    }
}
