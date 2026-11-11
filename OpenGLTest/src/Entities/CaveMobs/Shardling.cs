using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Goblins;

namespace VoxelEngine.Entities.CaveMobs;

/// <summary>
/// The shardling: a crystal caster of the crystal caves. It keeps
/// <see cref="MobConfig.CaveShardlingMinRange"/>..<see cref="MobConfig.CaveShardlingMaxRange"/> from its target
/// (walking in from beyond, backing off from inside) but still casts at a target inside the band, and, standing on
/// the ground off its cooldown with a clear line to the chest, plays its cast clip aimed at that chest; the shard
/// leaves the hand at the clip's <see cref="ReleaseEvent"/> (<see cref="TryTakeShotRelease"/>), which the manager
/// launches as a chilling projectile. Main thread only.
/// </summary>
public sealed class Shardling : CaveMob
{
    /// <summary>The entity file name (<c>assets/data/entities/shardling.json</c>).</summary>
    public const string EntityName = "shardling";

    /// <summary>The cast clip, named like the goblin's so the shared clip tooling reads both.</summary>
    public const string ThrowClip = "throw";

    /// <inheritdoc />
    public override string AttackClip => ThrowClip;

    /// <summary>Timeline event on the cast clip at which the shard leaves the hand.</summary>
    public const string ReleaseEvent = "release";

    private float _castCooldown = MobConfig.CaveShardlingFirstCastDelaySeconds;
    private readonly AimTracker _aim = new();

    public Shardling(EntityType type, Vector3 position, int tier, bool spawnIn = true)
        : base(type, position, tier, MobConfig.CaveShardlingWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.CaveShardlingHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.CaveShardlingHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.CaveShardlingWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.CaveShardlingJumpSpeed;

    /// <summary>The damage one shard deals at this shardling's tier.</summary>
    public int ShardDamage => CaveDifficulty.Scale(MobConfig.CaveShardDamage, Tier);

    /// <summary>Where a shard leaves the hand: a little in front of the body at shoulder height.</summary>
    public Vector3 ShotOrigin => Position + Forward * 0.4f + new Vector3(0f, BodyHeight * 0.3f, 0f);

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk and a cast clip
    /// carrying the release event.</summary>
    public static void ValidateContent(EntityType type) =>
        EntityContract.RequireHumanoid(type, "Shardling", ThrowClip, ReleaseEvent);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _castCooldown = MathF.Max(0f, _castCooldown - scaledDelta);
        _aim.Track(scaledDelta, Animation.IsActionPlaying);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        // Cast at any in-reach target, including inside the band: hugging a caster must never leave it harmless.
        if (dist <= MobConfig.CaveShardlingMaxRange && IsGrounded && _castCooldown <= 0f
            && SeesTarget(ShotOrigin, target, chunks))
        {
            _aim.Lock(target, chunks);
            StartAttack();
            var jitter = (Random.Shared.NextSingle() - 0.5f) * 2f * MobConfig.CaveShardlingCooldownJitter;
            _castCooldown = MobConfig.CaveShardlingCooldownSeconds + jitter;
            return Vector3.Zero;
        }

        if (dist > MobConfig.CaveShardlingMaxRange) return toward;
        if (dist < MobConfig.CaveShardlingMinRange) return -toward;

        // Inside the band, off the cooldown: keep walking at the target until the line clears.
        return SeesTarget(ShotOrigin, target, chunks) ? Vector3.Zero : toward;
    }

    /// <summary>
    /// True once per cast, at the cast clip's release event: where the shard leaves the hand and the launch velocity
    /// that lobs it onto the chest of the target where the shard will meet it (<see cref="AimTracker"/>; a
    /// maximum-range toss when out of reach).
    /// </summary>
    public bool TryTakeShotRelease(out Vector3 origin, out Vector3 velocity)
    {
        origin = default;
        velocity = default;
        if (!Animation.ConsumeEvent(ReleaseEvent)) return false;

        origin = ShotOrigin;
        _aim.Solve(origin, MobConfig.CaveShardSpeed, ThrownProjectile.Gravity, out velocity);
        return true;
    }
}
