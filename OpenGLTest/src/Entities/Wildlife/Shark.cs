using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The shark: the open sea's hunter, about three and a half blocks long. It cruises on its <c>swim</c> clip; a player
/// within <see cref="WildlifeConfig.SharkNoticeRangeBlocks"/> is prey: it charges (its <c>charge</c> clip, at
/// <see cref="WildlifeConfig.SharkChargeSpeedFactor"/> of its cruise) at their chest and, off its cooldown within
/// <see cref="WildlifeConfig.SharkBiteReach"/> of it in 3D, plays <c>bite</c>, lunging while the clip plays; at the
/// clip's <see cref="ImpactEvent"/> its jaws land (<see cref="TryTakeStrike"/>). It never leaves the water, so a
/// player on the shore is only bitten at the water's edge. Main thread only.
/// </summary>
public sealed class Shark : AquaticAnimal
{
    /// <summary>The entity file name (<c>assets/data/entities/shark.json</c>).</summary>
    public const string EntityName = "shark";

    public const string ChargeClip = "charge";
    public const string BiteClip = "bite";

    /// <summary>Timeline event on the bite clip at which the jaws close.</summary>
    public const string ImpactEvent = "impact";

    private static readonly string[] Parts = ["Body", "Head", "Jaw", "Tail1"];

    /// <summary>How the shark swims.</summary>
    public static readonly FlightTuning Tuning = new(
        WildlifeConfig.SharkCruiseSpeed, WildlifeConfig.SharkVerticalSpeed, WildlifeConfig.SharkAcceleration,
        WildlifeConfig.SharkHoverMinBlocks, WildlifeConfig.SharkHoverMaxBlocks, WildlifeConfig.SharkMinClearanceBlocks,
        WildlifeConfig.SharkBobBlocks, WildlifeConfig.SharkBobHz);

    private float _biteCooldown;

    public Shark(EntityType type, Vector3 position, bool spawnIn = true) : base(type, position, Tuning, spawnIn)
    {
    }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Shark;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.SharkHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.SharkHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.SharkShoveSpeed;

    // A hunter worth watching: its bar always shows
    /// <inheritdoc />
    public override bool ShowsHealthbar => true;

    /// <inheritdoc />
    public override string AttackClip => BiteClip;

    /// <inheritdoc />
    protected override string? DartClip => ChargeClip;

    /// <inheritdoc />
    protected override float DartSpeed => WildlifeConfig.SharkCruiseSpeed * WildlifeConfig.SharkChargeSpeedFactor;

    /// <summary>Whether a shark centered at <paramref name="center"/> bites a player whose eye is at
    /// <paramref name="eye"/>: their chest within <see cref="WildlifeConfig.SharkBiteReach"/> in 3D.</summary>
    public static bool InBiteReach(Vector3 center, Vector3 eye) =>
        Vector3.DistanceSquared(center, ChestOf(eye)) <= WildlifeConfig.SharkBiteReach * WildlifeConfig.SharkBiteReach;

    /// <summary>Throws, naming the model file, unless the model has the shark rig, the idle, swim and charge clips
    /// and a bite clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        RequireSwimmer(type, "Shark", Parts);
        EntityContract.RequireClip(type, "Shark", ChargeClip);
        EntityContract.RequireEvent(type, "Shark", EntityContract.RequireClip(type, "Shark", BiteClip), ImpactEvent);
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        NearestThreatSquared(targets)
        <= WildlifeConfig.SharkNoticeRangeBlocks * WildlifeConfig.SharkNoticeRangeBlocks;

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!InBiteReach(Position, target.Eye) || _biteCooldown > 0f) return toward;

        StartAttack();
        _biteCooldown = WildlifeConfig.SharkBiteCooldownSeconds;
        return Vector3.Zero;
    }

    /// <inheritdoc />
    protected override void SwimToward(IMobTarget? target, Vector3? wander, float deltaTime, ChunkManager chunks)
    {
        Darting = target != null;
        if (target == null)
        {
            Swim(wander, chasing: false, wander == null ? 1f : MobConfig.MobWanderSpeedFactor, deltaTime, chunks);
            return;
        }

        var lunging = Animation.IsPlaying(BiteClip);
        Swim(ChestOf(target.Eye), chasing: true,
            lunging ? WildlifeConfig.SharkLungeSpeedFactor : WildlifeConfig.SharkChargeSpeedFactor, deltaTime, chunks);
    }

    /// <inheritdoc />
    public override bool TryTakeStrike() => Animation.ConsumeEvent(ImpactEvent);

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) => InBiteReach(Position, eye);

    /// <inheritdoc />
    public override int StrikeDamage => WildlifeConfig.SharkBiteDamage;

    /// <inheritdoc />
    public override float StrikeKnockbackSpeed => WildlifeConfig.SharkKnockbackSpeed;

    /// <inheritdoc />
    public override float StrikeKnockbackLift => WildlifeConfig.SharkKnockbackLift;
}
