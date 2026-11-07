using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.SkyMobs;

/// <summary>
/// The cloud golem: a slow, very tough ground bruiser of the big sky zones, harder than a first-layer
/// hellbrute. It walks at its target (keeping the shared ledge caution, so it stays on its island unless the
/// target is below) and, in <see cref="MobConfig.CloudgolemSlamTriggerReach"/> and off its cooldown, plays its
/// slam clip. At the clip's <see cref="ImpactEvent"/> (<see cref="TryTakeSlamImpact"/>) the shockwave hits every
/// target the manager finds inside it (<see cref="InShockwave"/>), with a big shove. Main thread only.
/// </summary>
public sealed class Cloudgolem : SkyMob
{
    /// <summary>The entity file name (<c>assets/data/entities/cloudgolem.json</c>).</summary>
    public const string EntityName = "cloudgolem";

    public const string SlamClip = "slam";

    /// <summary>Timeline event on the slam clip at which the fists hit the ground and the shockwave goes out.</summary>
    public const string ImpactEvent = "impact";

    private float _slamCooldown;

    public Cloudgolem(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, MobConfig.CloudgolemWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.CloudgolemHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.CloudgolemHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.CloudgolemWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.CloudgolemJumpSpeed;

    /// <summary>The damage the shockwave deals in this golem's zone.</summary>
    public int SlamDamage => SkyDifficulty.Scale(MobConfig.CloudgolemSlamDamage, Tier);

    /// <summary>Whether a golem centered at <paramref name="center"/> starts a slam at a player whose eye is at
    /// <paramref name="eye"/>.</summary>
    public static bool InSlamReach(Vector3 center, Vector3 eye) =>
        MobTargets.InMeleeReach(center, eye, MobConfig.CloudgolemSlamTriggerReach);

    /// <summary>
    /// Whether the shockwave of a golem standing at <paramref name="feet"/> reaches a player whose eye is at
    /// <paramref name="eye"/>: within <see cref="MobConfig.CloudgolemShockwaveRadius"/> horizontally, its feet
    /// within <see cref="MobConfig.CloudgolemShockwaveHeight"/> of the golem's.
    /// </summary>
    public static bool InShockwave(Vector3 feet, Vector3 eye)
    {
        var dx = eye.X - feet.X;
        var dz = eye.Z - feet.Z;
        const float radius = MobConfig.CloudgolemShockwaveRadius;
        if (dx * dx + dz * dz > radius * radius) return false;
        return MathF.Abs(eye.Y - Camera.EyeHeight - feet.Y) <= MobConfig.CloudgolemShockwaveHeight;
    }

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk and a slam clip
    /// carrying the impact event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireHumanoid(type, "Cloudgolem", SlamClip, ImpactEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _slamCooldown = MathF.Max(0f, _slamCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InSlamReach(Position, target.Eye))
        {
            if (_slamCooldown > 0f) return Vector3.Zero;
            Animation.Play(SlamClip);
            _slamCooldown = MobConfig.CloudgolemSlamCooldownSeconds;
            return Vector3.Zero;
        }

        return dist <= MobConfig.CloudgolemStandoffBlocks ? Vector3.Zero : toward;
    }

    /// <summary>True once per slam, at the slam clip's impact event; the manager finds the targets the
    /// shockwave reaches.</summary>
    public bool TryTakeSlamImpact() => Animation.ConsumeEvent(ImpactEvent);
}
