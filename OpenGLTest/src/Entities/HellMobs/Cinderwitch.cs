using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Goblins;

namespace VoxelEngine.Entities.HellMobs;

/// <summary>
/// The cinderwitch: a ranged fire caster of hell's pockets. It keeps
/// <see cref="MobConfig.HellCasterMinRange"/>..<see cref="MobConfig.HellCasterMaxRange"/> from its
/// target (walking in from beyond, backing off from inside) and, standing on the ground off its
/// cooldown with a clear line to the chest, plays its cast clip aimed at that chest; the fire bolt
/// leaves the hand at the clip's <see cref="ReleaseEvent"/> (<see cref="TryTakeCastRelease"/>), which
/// the manager launches as a burning projectile. Its cooldown shortens with the layer
/// (<see cref="LayerDifficulty.Cooldown"/>). Main thread only.
/// </summary>
public sealed class Cinderwitch : HellMob
{
    /// <summary>The entity file name (<c>assets/data/entities/cinderwitch.json</c>).</summary>
    public const string EntityName = "cinderwitch";

    /// <summary>The cast clip, named like the goblin's so the shared clip tooling reads both.</summary>
    public const string CastClip = "throw";

    /// <summary>Timeline event on the cast clip at which the bolt leaves the hand.</summary>
    public const string ReleaseEvent = "release";

    private float _castCooldown = MobConfig.HellCasterFirstCastDelaySeconds;
    private Vector3 _aimTarget;

    public Cinderwitch(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, MobConfig.HellCasterWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.HellCasterHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.HellCasterHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.HellCasterWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.HellCasterJumpSpeed;

    /// <summary>The damage one bolt deals on this caster's layer.</summary>
    public int BoltDamage => LayerDifficulty.Scale(MobConfig.HellBoltDamage, Layer);

    /// <summary>Where a bolt leaves the hand: a little in front of the body at shoulder height.</summary>
    public Vector3 CastOrigin => Position + Forward * 0.4f + new Vector3(0f, BodyHeight * 0.3f, 0f);

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk and a
    /// cast clip carrying the release event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireHumanoid(type, "Cinderwitch", CastClip, ReleaseEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _castCooldown = MathF.Max(0f, _castCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (dist > MobConfig.HellCasterMaxRange) return toward;
        if (dist < MobConfig.HellCasterMinRange) return -toward;

        // Inside the band: cast when grounded (so a jump never lifts the hand over a wall), off the
        // cooldown and with a clear line; otherwise keep walking at the target until the line clears.
        if (!SeesTarget(CastOrigin, target, chunks)) return toward;
        if (!IsGrounded || _castCooldown > 0f) return Vector3.Zero;

        _aimTarget = ChestOf(target.Eye);
        Animation.Play(CastClip);
        var jitter = (Random.Shared.NextSingle() - 0.5f) * 2f * MobConfig.HellCasterCooldownJitter;
        _castCooldown = LayerDifficulty.Cooldown(MobConfig.HellCasterCooldownSeconds + jitter, Layer);
        return Vector3.Zero;
    }

    /// <summary>
    /// True once per cast, at the cast clip's release event: where the bolt leaves the hand and the
    /// launch velocity that lobs it onto the chest the cast was aimed at (GoblinAim's low arc, a
    /// maximum-range toss toward it when out of reach).
    /// </summary>
    public bool TryTakeCastRelease(out Vector3 origin, out Vector3 velocity)
    {
        origin = default;
        velocity = default;
        if (!Animation.ConsumeEvent(ReleaseEvent)) return false;

        origin = CastOrigin;
        GoblinAim.TryLaunchVelocity(origin, _aimTarget, MobConfig.HellBoltSpeed, ThrownProjectile.Gravity,
            out velocity);
        return true;
    }
}
