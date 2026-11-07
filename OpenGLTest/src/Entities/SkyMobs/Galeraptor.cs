using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.SkyMobs;

/// <summary>
/// The galeraptor: a diving flyer of the sky islands (a <see cref="RiggedMob.Fly"/> kind, <see cref="Tuning"/>).
/// It hovers <see cref="RiggedMob.HoverHeight"/> above its target's eye and, off its cooldown with a clear
/// line within <see cref="MobConfig.GaleraptorDiveRangeBlocks"/> horizontally, plays its dive clip and stoops
/// at the target's chest at <see cref="MobConfig.GaleraptorDiveSpeedFactor"/> of its flight. The strike lands
/// at the clip's <see cref="ImpactEvent"/> (<see cref="TryTakeDiveImpact"/>), where the manager bills it,
/// scaled by the zone, only to a target still in reach (<see cref="InDiveReach"/>); then it climbs back to its
/// hover. Main thread only.
/// </summary>
public sealed class Galeraptor : SkyMob
{
    /// <summary>The entity file name (<c>assets/data/entities/galeraptor.json</c>).</summary>
    public const string EntityName = "galeraptor";

    public const string DiveClip = "dive";

    /// <inheritdoc />
    public override string AttackClip => DiveClip;

    /// <summary>Timeline event on the dive clip at which the talons strike.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>How the galeraptor flies.</summary>
    public static readonly FlightTuning Tuning = new(
        MobConfig.GaleraptorCruiseSpeed, MobConfig.GaleraptorVerticalSpeed, MobConfig.GaleraptorAcceleration,
        MobConfig.GaleraptorHoverMinBlocks, MobConfig.GaleraptorHoverMaxBlocks, MobConfig.GaleraptorMinClearanceBlocks,
        MobConfig.GaleraptorBobBlocks, MobConfig.GaleraptorBobHz);

    private float _diveCooldown = MobConfig.GaleraptorFirstDiveDelaySeconds;

    public Galeraptor(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, MobConfig.GaleraptorCruiseSpeed, spawnIn, Tuning)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.GaleraptorHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.GaleraptorHalfExtent;

    // A flyer never walks: Locomote steers its flight instead, so these only fill the walker's slots
    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.GaleraptorCruiseSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    /// <summary>Whether the dive clip is playing.</summary>
    public bool IsDiving => Animation.IsPlaying(DiveClip);

    /// <summary>The damage one strike deals in this galeraptor's zone.</summary>
    public int DiveDamage => SkyDifficulty.Scale(MobConfig.GaleraptorDiveDamage, Tier);

    /// <summary>Whether a galeraptor centered at <paramref name="center"/> can rake a player whose eye is at
    /// <paramref name="eye"/>: its chest within <see cref="MobConfig.GaleraptorDiveReach"/> in 3D.</summary>
    public static bool InDiveReach(Vector3 center, Vector3 eye) =>
        Vector3.DistanceSquared(center, ChestOf(eye)) <= MobConfig.GaleraptorDiveReach * MobConfig.GaleraptorDiveReach;

    /// <summary>Throws, naming the model file, unless the model is a flyer's rig with idle, fly and a dive clip
    /// carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireFlyer(type, "Galeraptor", DiveClip, ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _diveCooldown = MathF.Max(0f, _diveCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (IsCarrying || _diveCooldown > 0f || dist > MobConfig.GaleraptorDiveRangeBlocks || !SeesTarget(Position, target, chunks))
            return Vector3.Zero;

        StartAttack();
        _diveCooldown = MobConfig.GaleraptorDiveCooldownSeconds;
        return Vector3.Zero;
    }

    /// <summary>Flies at the chest while diving, else to the hover over its target, its wander goal, or holds
    /// its hover over the floor.</summary>
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        if (Carried != null)
        {
            // Climb with the prize: the goal sits above the carried target, never into a new dive
            var up = new Vector3(0f, MobConfig.GaleraptorCarryHeightBlocks, 0f);
            Fly(Carried.Eye + up, chasing: true, MoveFactor, deltaTime, chunks);
            return;
        }

        if (target != null)
        {
            var diving = IsDiving;
            var goal = diving ? ChestOf(target.Eye) : FlightSteering.ChaseGoal(target.Eye, HoverHeight);
            Fly(goal, chasing: true, (diving ? MobConfig.GaleraptorDiveSpeedFactor : 1f) * MoveFactor, deltaTime, chunks);
            return;
        }

        Fly(wander, chasing: false, (wander == null ? 1f : MobConfig.MobWanderSpeedFactor) * MoveFactor, deltaTime, chunks);
    }

    /// <summary>True once per dive, at the dive clip's impact event; the manager decides whether the target is
    /// still in reach.</summary>
    public bool TryTakeDiveImpact() => Animation.ConsumeEvent(ImpactEvent);

    /// <summary>The target the talons hold (lifting it skyward before the drop), or null.</summary>
    public IMobTarget? Carried { get; private set; }

    private float _carrySecondsLeft;
    private float _grabY;

    /// <summary>Whether the raptor is carrying a target up.</summary>
    public bool IsCarrying => Carried != null;

    /// <summary>Takes hold of <paramref name="target"/>: the manager lifts it until it is
    /// <see cref="MobConfig.GaleraptorCarryHeightBlocks"/> above where it was grabbed, then lets go.</summary>
    public void Grab(IMobTarget target)
    {
        Carried = target;
        _carrySecondsLeft = MobConfig.GaleraptorCarryMaxSeconds;
        _grabY = target.Eye.Y;
    }

    /// <summary>Lets go of whatever is held.</summary>
    public void Release() => Carried = null;

    /// <summary>Ages the hold and reports whether the carried target should now be dropped: it reached the
    /// carry height, the time ran out, it became immune or dead, or the raptor itself is going down.</summary>
    public bool CarryDone(float deltaTime)
    {
        if (Carried == null) return false;
        _carrySecondsLeft -= deltaTime;
        return _carrySecondsLeft <= 0f || Carried.IsDead || Carried.IsImmune || IsDying
               || Carried.Eye.Y - _grabY >= MobConfig.GaleraptorCarryHeightBlocks;
    }
}
