using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.CaveMobs;

/// <summary>
/// The sporeling: a squat fungal walker of the fungal caves. It walks at its target and, in
/// <see cref="MobConfig.CaveSporelingTriggerReach"/> and off its cooldown, plays its melee clip; at the clip's
/// <see cref="ImpactEvent"/> (<see cref="TryTakeBurstImpact"/>) a spore burst goes out, which the manager bills to
/// every target inside it (<see cref="InBurst"/>) and fills with spores. Main thread only.
/// </summary>
public sealed class Sporeling : CaveMob
{
    /// <summary>The entity file name (<c>assets/data/entities/sporeling.json</c>).</summary>
    public const string EntityName = "sporeling";

    public const string MeleeClip = "melee";

    /// <inheritdoc />
    public override string AttackClip => MeleeClip;

    /// <summary>Timeline event on the melee clip at which the spore burst goes out.</summary>
    public const string ImpactEvent = "impact";

    private float _burstCooldown;

    public Sporeling(EntityType type, Vector3 position, int tier, bool spawnIn = true)
        : base(type, position, tier, MobConfig.CaveSporelingWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.CaveSporelingHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.CaveSporelingHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.CaveSporelingWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.CaveSporelingJumpSpeed;

    /// <summary>The damage the spore burst deals at this sporeling's tier.</summary>
    public int BurstDamage => CaveDifficulty.Scale(MobConfig.CaveSporeBurstDamage, Tier);

    /// <summary>Whether a sporeling centered at <paramref name="center"/> starts a burst at a player whose eye is at
    /// <paramref name="eye"/>.</summary>
    public static bool InTriggerReach(Vector3 center, Vector3 eye) =>
        MobTargets.InMeleeReach(center, eye, MobConfig.CaveSporelingTriggerReach);

    /// <summary>
    /// Whether the burst of a sporeling standing at <paramref name="feet"/> reaches a player whose eye is at
    /// <paramref name="eye"/>: within <see cref="MobConfig.CaveSporeBurstRadius"/> horizontally, its feet within
    /// <see cref="MobConfig.CaveSporeBurstHeight"/> of the sporeling's.
    /// </summary>
    public static bool InBurst(Vector3 feet, Vector3 eye) =>
        MobTargets.InShockwave(feet, eye, MobConfig.CaveSporeBurstRadius, MobConfig.CaveSporeBurstHeight);

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk and a melee clip
    /// carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireHumanoid(type, "Sporeling", MeleeClip, ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _burstCooldown = MathF.Max(0f, _burstCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InTriggerReach(Position, target.Eye))
        {
            if (_burstCooldown > 0f) return Vector3.Zero;
            StartAttack();
            _burstCooldown = MobConfig.CaveSporelingCooldownSeconds;
            return Vector3.Zero;
        }

        return dist <= MobConfig.CaveSporelingStandoffBlocks ? Vector3.Zero : toward;
    }

    /// <summary>True once per burst, at the melee clip's impact event; the manager finds the targets it reaches.</summary>
    public bool TryTakeBurstImpact() => Animation.ConsumeEvent(ImpactEvent);
}
