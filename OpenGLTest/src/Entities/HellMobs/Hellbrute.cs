using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.HellMobs;

/// <summary>
/// The hellbrute: a slow, tanky melee bruiser of hell's pockets. It walks at its target and, once
/// it sees it (a clear line from its eye to the chest) within
/// <see cref="MobConfig.HellbruteChargeRangeBlocks"/>, charges at
/// <see cref="MobConfig.HellbruteChargeSpeedFactor"/> of its walk. In reach and off its cooldown it
/// plays its melee clip; the blow lands at the clip's <see cref="ImpactEvent"/>
/// (<see cref="TryTakeMeleeImpact"/>), where the manager bills it, scaled by the layer, only to a
/// target still in reach. Main thread only.
/// </summary>
public sealed class Hellbrute : HellMob
{
    /// <summary>The entity file name (<c>assets/data/entities/hellbrute.json</c>).</summary>
    public const string EntityName = "hellbrute";

    public const string MeleeClip = "melee";

    /// <summary>Timeline event on the melee clip at which the blow connects.</summary>
    public const string ImpactEvent = "impact";

    private float _meleeCooldown;
    private bool _charging;

    public Hellbrute(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, MobConfig.HellbruteWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.HellbruteHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.HellbruteHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.HellbruteWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.HellbruteJumpSpeed;

    /// <inheritdoc />
    protected override float GroundSpeedFactor => _charging ? MobConfig.HellbruteChargeSpeedFactor : 1f;

    /// <summary>Whether the brute is charging its target this frame.</summary>
    public bool IsCharging => _charging;

    /// <summary>The damage one blow deals on this brute's layer.</summary>
    public int MeleeDamage => LayerDifficulty.Scale(MobConfig.HellbruteMeleeDamage, Layer);

    /// <summary>Whether a brute centered at <paramref name="position"/> can land its blow on a player
    /// whose eye is at <paramref name="eye"/>.</summary>
    public static bool InMeleeReach(Vector3 position, Vector3 eye) =>
        MobTargets.InMeleeReach(position, eye, MobConfig.HellbruteMeleeReach);

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk and a
    /// melee clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireHumanoid(type, "Hellbrute", MeleeClip, ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _meleeCooldown = MathF.Max(0f, _meleeCooldown - scaledDelta);
        // Decided afresh every frame it chases: a swing, a stroll or a death never keeps the charge
        _charging = false;
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InMeleeReach(Position, target.Eye))
        {
            if (_meleeCooldown > 0f) return Vector3.Zero;
            Animation.Play(MeleeClip);
            _meleeCooldown = MobConfig.HellbruteMeleeCooldownSeconds;
            return Vector3.Zero;
        }

        if (dist <= MobConfig.HellbruteStandoffBlocks) return Vector3.Zero;
        _charging = IsGrounded && dist <= MobConfig.HellbruteChargeRangeBlocks && SeesTarget(EyePoint, target, chunks);
        return toward;
    }

    /// <summary>True once per swing, at the melee clip's impact event; the manager decides whether the
    /// target is still in reach.</summary>
    public bool TryTakeMeleeImpact() => Animation.ConsumeEvent(ImpactEvent);
}
