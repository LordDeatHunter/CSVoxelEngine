using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The corpse bloom: the jungle's rooted carrion flower. It never moves. Shut (its <c>closed</c> loop) until a player
/// comes within <see cref="WildlifeConfig.BloomWakeRangeBlocks"/>, it opens (<c>idle</c>) and, with a clear line
/// within <see cref="WildlifeConfig.BloomTriggerRadiusBlocks"/> and off its cooldown, plays its <c>spew</c> clip:
/// at the clip's <see cref="ImpactEvent"/> a poison burst goes out (<see cref="TryTakeStrike"/>,
/// <see cref="StrikeReaches"/>) and leaves a lingering cloud (<see cref="TakeCloudPulse"/>,
/// <see cref="CloudReaches"/>) that poisons whoever stands in it every
/// <see cref="WildlifeConfig.BloomCloudPulseSeconds"/> for <see cref="WildlifeConfig.BloomCloudSeconds"/>. The
/// manager bills both (<c>WildlifeManager.Attack</c>); the bloom is never hurt by its own cloud, which only
/// reaches targets. Whether it is open rides <see cref="Posture"/>, so a client's mirror opens and shuts with
/// it. Main thread only.
/// </summary>
public sealed class CorpseBloom : Animal
{
    /// <summary>The entity file name (<c>assets/data/entities/corpse_bloom.json</c>).</summary>
    public const string EntityName = "corpse_bloom";

    public const string ClosedClip = "closed";
    public const string SpewClip = "spew";

    /// <summary>Timeline event on the spew clip at which the burst goes out.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>The status effect its poison gives (<c>assets/data/effects/poison.json</c>).</summary>
    public const string PoisonEffect = "poison";

    private bool _open;
    private bool _wokenThisFrame;
    private float _asleepSeconds;
    private float _spewCooldown;
    private float _cloudRemaining;
    private float _pulseIn;

    public CorpseBloom(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, 1f, spawnIn)
    {
        Animation.SetGait(ClosedClip, ClosedClip, 1f);
    }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.CorpseBloom;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.BloomHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.BloomHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => 0f;

    /// <inheritdoc />
    protected override float WalkSpeed => 1f;

    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    // Shut, it is just a plant: its bar shows once it is awake or hurt
    /// <inheritdoc />
    public override bool ShowsHealthbar => _open || base.ShowsHealthbar;

    /// <inheritdoc />
    public override string AttackClip => SpewClip;

    /// <summary>Whether the bloom is open (awake).</summary>
    public bool IsOpen => _open;

    /// <summary>Whether its poison cloud still hangs.</summary>
    public bool CloudActive => _cloudRemaining > 0f;

    /// <inheritdoc />
    public override byte Posture => _open ? (byte)1 : (byte)0;

    /// <inheritdoc />
    public override void MirrorPosture(byte posture) => SetOpen(posture != 0);

    /// <summary>Whether a bloom standing at <paramref name="feet"/> burst-poisons a player whose eye is at
    /// <paramref name="eye"/>: within <see cref="WildlifeConfig.BloomBurstRadiusBlocks"/> horizontally, the player's feet
    /// within <see cref="WildlifeConfig.BloomBurstHeightBlocks"/> of its own.</summary>
    public static bool InBurst(Vector3 feet, Vector3 eye) =>
        MobTargets.InShockwave(feet, eye, WildlifeConfig.BloomBurstRadiusBlocks, WildlifeConfig.BloomBurstHeightBlocks);

    /// <summary>Whether a player whose eye is at <paramref name="eye"/> stands in the cloud: the burst's reach, a little
    /// taller, since the cloud hangs and drifts up.</summary>
    public bool CloudReaches(Vector3 eye) =>
        CloudActive && MobTargets.InShockwave(Feet, eye, WildlifeConfig.BloomBurstRadiusBlocks,
            WildlifeConfig.BloomBurstHeightBlocks * 1.5f);

    /// <summary>True once per <see cref="WildlifeConfig.BloomCloudPulseSeconds"/> while the cloud hangs: the manager then
    /// poisons every target <see cref="CloudReaches"/> holds.</summary>
    public bool TakeCloudPulse()
    {
        if (!CloudActive || _pulseIn > 0f) return false;
        _pulseIn = WildlifeConfig.BloomCloudPulseSeconds;
        return true;
    }

    /// <summary>Throws, naming the model file, unless the model has the bloom's groups and its idle, closed and
    /// spew clips, the last carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireParts(type, "Corpse Bloom", ["Base", "Bulb"]);
        EntityContract.RequireClip(type, "Corpse Bloom", EntityAnimator.IdleClip);
        EntityContract.RequireClip(type, "Corpse Bloom", ClosedClip);
        EntityContract.RequireEvent(type, "Corpse Bloom",
            EntityContract.RequireClip(type, "Corpse Bloom", SpewClip), ImpactEvent);
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        NearestThreatSquared(targets) <= WildlifeConfig.BloomWakeRangeBlocks * WildlifeConfig.BloomWakeRangeBlocks;

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _spewCooldown = MathF.Max(0f, _spewCooldown - scaledDelta);
        _cloudRemaining = MathF.Max(0f, _cloudRemaining - scaledDelta);
        _pulseIn = MathF.Max(0f, _pulseIn - scaledDelta);

        // It shuts again once nobody has been near for a while
        _asleepSeconds = _wokenThisFrame ? 0f : _asleepSeconds + scaledDelta;
        _wokenThisFrame = false;
        if (_open && _asleepSeconds >= WildlifeConfig.BloomCloseDelaySeconds) SetOpen(false);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        _wokenThisFrame = true;
        SetOpen(true);
        if (dist > WildlifeConfig.BloomTriggerRadiusBlocks || _spewCooldown > 0f
            || !SeesTarget(EyePoint, target, chunks))
            return Vector3.Zero;

        StartAttack();
        _spewCooldown = WildlifeConfig.BloomSpewCooldownSeconds;
        return Vector3.Zero;
    }

    // Rooted: it takes gravity and the ground but never a step
    /// <inheritdoc />
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks) =>
        base.Locomote(Vector3.Zero, target, null, deltaTime, chunks);

    /// <inheritdoc />
    public override bool TryTakeStrike()
    {
        if (!Animation.ConsumeEvent(ImpactEvent)) return false;
        _cloudRemaining = WildlifeConfig.BloomCloudSeconds;
        _pulseIn = WildlifeConfig.BloomCloudPulseSeconds;
        return true;
    }

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) => InBurst(Feet, eye);

    /// <inheritdoc />
    public override int StrikeDamage => WildlifeConfig.BloomBurstDamage;

    /// <inheritdoc />
    public override string? StrikeEffect => PoisonEffect;

    private void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        Animation.SetGait(open ? EntityAnimator.IdleClip : ClosedClip, open ? EntityAnimator.IdleClip : ClosedClip, 1f);
    }
}