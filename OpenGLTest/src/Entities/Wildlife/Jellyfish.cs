using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The jellyfish: a drifting bell with stinging tentacles, harmless until touched. It never chases: it drifts to its
/// wander points at <see cref="WildlifeConfig.JellyfishDriftSpeed"/> on its <c>swim</c> pulse. A player within
/// <see cref="WildlifeConfig.JellyfishStingReach"/> of its bell in 3D, off its cooldown, makes it play <c>sting</c>;
/// at the clip's <see cref="ImpactEvent"/> every player still in reach is stung (<see cref="TryTakeStrike"/>) and
/// poisoned with <see cref="VenomEffect"/>. Its skin is opaque: translucency is a later rendering decision. Main
/// thread only.
/// </summary>
public sealed class Jellyfish : AquaticAnimal
{
    /// <summary>The entity file name (<c>assets/data/entities/jellyfish.json</c>).</summary>
    public const string EntityName = "jellyfish";

    public const string StingClip = "sting";

    /// <summary>Timeline event on the sting clip at which the tentacles lash.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>The status effect its sting gives (<c>assets/data/effects/venom.json</c>).</summary>
    public const string VenomEffect = "venom";

    private static readonly string[] Parts = ["Bell", "BellRim", "Arm1", "Tentacle1"];

    /// <summary>How the jellyfish drifts.</summary>
    public static readonly FlightTuning Tuning = new(
        WildlifeConfig.JellyfishDriftSpeed, WildlifeConfig.JellyfishVerticalSpeed, WildlifeConfig.JellyfishAcceleration,
        WildlifeConfig.JellyfishHoverMinBlocks, WildlifeConfig.JellyfishHoverMaxBlocks,
        WildlifeConfig.JellyfishMinClearanceBlocks, WildlifeConfig.JellyfishBobBlocks, WildlifeConfig.JellyfishBobHz);

    private float _stingCooldown;

    public Jellyfish(EntityType type, Vector3 position, bool spawnIn = true) : base(type, position, Tuning, spawnIn)
    {
    }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Jellyfish;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.JellyfishHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.JellyfishHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.JellyfishShoveSpeed;

    /// <inheritdoc />
    public override string AttackClip => StingClip;

    /// <summary>Whether a jellyfish centered at <paramref name="center"/> stings a player whose eye is at
    /// <paramref name="eye"/>: their chest within <see cref="WildlifeConfig.JellyfishStingReach"/> in 3D.</summary>
    public static bool InStingReach(Vector3 center, Vector3 eye) =>
        Vector3.DistanceSquared(center, ChestOf(eye))
        <= WildlifeConfig.JellyfishStingReach * WildlifeConfig.JellyfishStingReach;

    /// <summary>Throws, naming the model file, unless the model has the bell and its tentacles, the idle and swim
    /// clips and a sting clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        RequireSwimmer(type, "Jellyfish", Parts);
        EntityContract.RequireEvent(type, "Jellyfish", EntityContract.RequireClip(type, "Jellyfish", StingClip), ImpactEvent);
    }

    // It only reacts to somebody within touching distance (twice the sting reach, so a drifting body closes the
    // gap); the target it is handed decides its sting and nothing else
    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        NearestThreatSquared(targets) <= WildlifeConfig.JellyfishStingReach * WildlifeConfig.JellyfishStingReach * 4f;

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _stingCooldown = MathF.Max(0f, _stingCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (_stingCooldown > 0f || !InStingReach(Position, target.Eye)) return Vector3.Zero;

        StartAttack();
        _stingCooldown = WildlifeConfig.JellyfishStingCooldownSeconds;
        return Vector3.Zero;
    }

    // Touched or not, it drifts: a target only decides its sting
    /// <inheritdoc />
    protected override void SwimToward(IMobTarget? target, Vector3? wander, float deltaTime, ChunkManager chunks) =>
        Swim(wander, chasing: false, wander == null ? 1f : MobConfig.MobWanderSpeedFactor, deltaTime, chunks);

    /// <inheritdoc />
    public override bool TryTakeStrike() => Animation.ConsumeEvent(ImpactEvent);

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) => InStingReach(Position, eye);

    /// <inheritdoc />
    public override int StrikeDamage => WildlifeConfig.JellyfishStingDamage;

    /// <inheritdoc />
    public override float StrikeKnockbackSpeed => WildlifeConfig.JellyfishKnockbackSpeed;

    /// <inheritdoc />
    public override float StrikeKnockbackLift => WildlifeConfig.JellyfishKnockbackLift;

    /// <inheritdoc />
    public override string? StrikeEffect => VenomEffect;
}
