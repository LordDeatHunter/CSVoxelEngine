using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.CaveMobs;

/// <summary>
/// The magmaborn: a hulking brute of cracked rock in the magma-touched deep. It walks at its target and, in reach
/// and off its cooldown, plays its melee clip; the blow lands at the clip's <see cref="ImpactEvent"/>
/// (<see cref="TryTakeMeleeImpact"/>), where the manager bills it, scaled by the tier, only to a target still in
/// reach, and sets it burning. Main thread only.
/// </summary>
public sealed class Magmaborn : CaveMob
{
    /// <summary>The entity file name (<c>assets/data/entities/magmaborn.json</c>).</summary>
    public const string EntityName = "magmaborn";

    public const string MeleeClip = "melee";

    /// <inheritdoc />
    public override string AttackClip => MeleeClip;

    /// <summary>Timeline event on the melee clip at which the blow connects.</summary>
    public const string ImpactEvent = "impact";

    private float _meleeCooldown;

    public Magmaborn(EntityType type, Vector3 position, int tier, bool spawnIn = true)
        : base(type, position, tier, MobConfig.CaveMagmabornWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.CaveMagmabornHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.CaveMagmabornHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.CaveMagmabornWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.CaveMagmabornJumpSpeed;

    /// <summary>The damage one blow deals at this magmaborn's tier.</summary>
    public int MeleeDamage => CaveDifficulty.Scale(MobConfig.CaveMagmabornMeleeDamage, Tier);

    /// <summary>Whether a magmaborn centered at <paramref name="center"/> can land its blow on a player whose eye is
    /// at <paramref name="eye"/>.</summary>
    public static bool InMeleeReach(Vector3 center, Vector3 eye) =>
        MobTargets.InMeleeReach(center, eye, MobConfig.CaveMagmabornMeleeReach);

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk and a melee clip
    /// carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireHumanoid(type, "Magmaborn", MeleeClip, ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _meleeCooldown = MathF.Max(0f, _meleeCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InMeleeReach(Position, target.Eye))
        {
            if (_meleeCooldown > 0f) return Vector3.Zero;
            StartAttack();
            _meleeCooldown = MobConfig.CaveMagmabornMeleeCooldownSeconds;
            return Vector3.Zero;
        }

        return dist <= MobConfig.CaveMagmabornStandoffBlocks ? Vector3.Zero : toward;
    }

    /// <summary>True once per swing, at the melee clip's impact event; the manager decides whether the target is
    /// still in reach.</summary>
    public bool TryTakeMeleeImpact() => Animation.ConsumeEvent(ImpactEvent);
}
