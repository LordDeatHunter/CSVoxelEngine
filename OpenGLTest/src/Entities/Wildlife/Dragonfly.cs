using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The dragonfly: a man-sized hunter of the waterside. It hovers and darts about, and now and then settles
/// on the ground on its six legs (its <c>perch</c> clip) before lifting off again. A player within
/// <see cref="WildlifeConfig.DragonflyNoticeRangeBlocks"/> is prey: it holds itself over their head and,
/// off its cooldown with a clear line within <see cref="WildlifeConfig.DragonflyDartRangeBlocks"/>, darts at
/// their chest in its <c>bite</c> clip, biting at the clip's <see cref="ImpactEvent"/> whoever is within
/// <see cref="WildlifeConfig.DragonflyBiteReach"/>. Main thread only.
/// </summary>
public sealed class Dragonfly : FlyingAnimal
{
    /// <summary>The entity file name (<c>assets/data/entities/dragonfly.json</c>).</summary>
    public const string EntityName = "dragonfly";

    public const string BiteClip = "bite";
    public const string PerchClip = "perch";
    public const string TakeoffClip = "takeoff";
    public const string LandClip = "land";

    /// <summary>Timeline event on the bite clip at which the jaws close.</summary>
    public const string ImpactEvent = "impact";

    // The counted action clips (RealmMob.StartAttack), which a client's mirror replays by this index
    private const int BiteAction = 0;
    private const int TakeoffActionIndex = 1;
    private const int LandActionIndex = 2;

    /// <summary>How the dragonfly flies.</summary>
    public static readonly FlightTuning Tuning = new(
        WildlifeConfig.DragonflyCruiseSpeed, WildlifeConfig.DragonflyVerticalSpeed, WildlifeConfig.DragonflyAcceleration,
        WildlifeConfig.DragonflyHoverMinBlocks, WildlifeConfig.DragonflyHoverMaxBlocks,
        WildlifeConfig.DragonflyMinClearanceBlocks, WildlifeConfig.DragonflyBobBlocks, WildlifeConfig.DragonflyBobHz);

    private float _biteCooldown = WildlifeConfig.DragonflyFirstBiteDelaySeconds;

    public Dragonfly(EntityType type, Vector3 position, bool spawnIn = true) : base(type, position, Tuning, spawnIn)
    {
    }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Dragonfly;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.DragonflyHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.DragonflyHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.DragonflyShoveSpeed;

    // A hunter worth watching: its bar always shows
    /// <inheritdoc />
    public override bool ShowsHealthbar => true;

    /// <inheritdoc />
    public override string AttackClip => BiteClip;

    /// <inheritdoc />
    protected override int AttackVariants => 3;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant switch
    {
        TakeoffActionIndex => TakeoffClip,
        LandActionIndex => LandClip,
        _ => BiteClip,
    };

    // On the ground its one clip is the perch, moving or not
    /// <inheritdoc />
    protected override (string Idle, string Move) GroundClips => (PerchClip, PerchClip);

    /// <inheritdoc />
    protected override (string Idle, string Move) AirClips(bool chasing) => (EntityAnimator.IdleClip, EntityAnimator.FlyClip);

    /// <inheritdoc />
    protected override int TakeoffAction => TakeoffActionIndex;

    /// <inheritdoc />
    protected override int LandAction => LandActionIndex;

    /// <summary>Whether a dragonfly centered at <paramref name="center"/> bites a player whose eye is at
    /// <paramref name="eye"/>: its chest within <see cref="WildlifeConfig.DragonflyBiteReach"/> in 3D.</summary>
    public static bool InBiteReach(Vector3 center, Vector3 eye) =>
        Vector3.DistanceSquared(center, ChestOf(eye))
        <= WildlifeConfig.DragonflyBiteReach * WildlifeConfig.DragonflyBiteReach;

    /// <summary>Throws, naming the model file, unless the model is a flyer's rig with idle, fly, perch, land and
    /// takeoff clips and a bite clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireFlyer(type, "Dragonfly", BiteClip, ImpactEvent);
        foreach (var clip in (string[])[PerchClip, TakeoffClip, LandClip])
            EntityContract.RequireClip(type, "Dragonfly", clip);
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        NearestThreatSquared(targets)
        <= WildlifeConfig.DragonflyNoticeRangeBlocks * WildlifeConfig.DragonflyNoticeRangeBlocks;

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (IsPerched || _biteCooldown > 0f || dist > WildlifeConfig.DragonflyDartRangeBlocks
            || !SeesTarget(Position, target, chunks))
            return Vector3.Zero;

        StartAttack(BiteAction);
        _biteCooldown = WildlifeConfig.DragonflyBiteCooldownSeconds;
        return Vector3.Zero;
    }

    /// <inheritdoc />
    protected override void FlyToward(IMobTarget? target, Vector3? wander, float deltaTime, ChunkManager chunks)
    {
        if (target != null)
        {
            var darting = Animation.IsPlaying(BiteClip);
            var goal = darting ? ChestOf(target.Eye) : FlightSteering.ChaseGoal(target.Eye, HoverHeight);
            Fly(goal, chasing: true, (darting ? WildlifeConfig.DragonflyDartSpeedFactor : 1f) * MoveFactor, deltaTime, chunks);
            return;
        }

        Fly(wander, chasing: false, (wander == null ? 1f : MobConfig.MobWanderSpeedFactor) * MoveFactor, deltaTime, chunks);
    }

    /// <inheritdoc />
    public override bool TryTakeStrike() => Animation.ConsumeEvent(ImpactEvent);

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) => InBiteReach(Position, eye);

    /// <inheritdoc />
    public override int StrikeDamage => WildlifeConfig.DragonflyBiteDamage;

    /// <inheritdoc />
    public override float StrikeKnockbackSpeed => WildlifeConfig.DragonflyKnockbackSpeed;

    /// <inheritdoc />
    public override float StrikeKnockbackLift => WildlifeConfig.DragonflyKnockbackLift;
}
