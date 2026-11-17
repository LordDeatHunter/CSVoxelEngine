using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The corpse bloom: the jungle's rooted carrion flower. It never moves. Shut (its <c>closed</c> loop) until a player
/// comes within <see cref="WildlifeConfig.BloomWakeRangeBlocks"/>, it opens (<c>idle</c>) and, with a clear line
/// within <see cref="WildlifeConfig.BloomTriggerRadiusBlocks"/> and off its cooldown, plays its <c>spew</c> clip:
/// at the clip's <see cref="ImpactEvent"/> a poison burst goes out (<see cref="TryTakeStrike"/>,
/// <see cref="StrikeReaches"/>) and leaves a lingering cloud (<see cref="TakeCloudPulse"/>,
/// <see cref="CloudReaches"/>, <see cref="CloudHolds"/>) that poisons whoever stands in it, player or mob, every
/// <see cref="WildlifeConfig.BloomCloudPulseSeconds"/> for <see cref="WildlifeConfig.BloomCloudSeconds"/>, fading as
/// it goes (<see cref="CloudStrength"/>). The manager bills both (<c>WildlifeManager.Attack</c>); a kind whose
/// entity file lists <c>poison</c> under <c>immune</c> (the bloom itself, the dragonfly) is never poisoned. Whether
/// it is open and how much cloud is left ride <see cref="Posture"/>, so a client's mirror opens, shuts and sheds
/// the cloud with it. Main thread only.
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

    private const int OpenBit = 1;

    // The steps the cloud's strength rides the posture byte in, above the open bit
    private const int CloudLevels = 7;

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

    /// <summary>How much of the cloud is left, 1 right after the burst falling to 0 as it fades: what thins the
    /// particles it sheds (<c>AmbientEffects.UpdateBloomClouds</c>). The poison keeps its full reach until 0.</summary>
    public float CloudStrength => MathUtils.Clamp01(_cloudRemaining / WildlifeConfig.BloomCloudSeconds);

    // Bit 0 says whether it is open; the bits above carry the cloud's strength in CloudLevels steps, rounded up so
    // a mirror keeps a cloud for as long as one hangs
    /// <inheritdoc />
    public override byte Posture =>
        (byte)((_open ? OpenBit : 0) | ((int)MathF.Ceiling(CloudStrength * CloudLevels) << 1));

    /// <inheritdoc />
    public override void MirrorPosture(byte posture)
    {
        SetOpen((posture & OpenBit) != 0);
        _cloudRemaining = WildlifeConfig.BloomCloudSeconds * Math.Min(posture >> 1, CloudLevels) / CloudLevels;
    }

    /// <summary>Whether a bloom standing at <paramref name="feet"/> burst-poisons a player whose eye is at
    /// <paramref name="eye"/>: within <see cref="WildlifeConfig.BloomBurstRadiusBlocks"/> horizontally, the player's feet
    /// within <see cref="WildlifeConfig.BloomBurstHeightBlocks"/> of its own.</summary>
    public static bool InBurst(Vector3 feet, Vector3 eye) =>
        MobTargets.InShockwave(feet, eye, WildlifeConfig.BloomBurstRadiusBlocks, WildlifeConfig.BloomBurstHeightBlocks);

    /// <summary>Whether a player whose eye is at <paramref name="eye"/> stands in the cloud: the burst's reach, a little
    /// taller, since the cloud hangs and drifts up.</summary>
    public bool CloudReaches(Vector3 eye) => CloudHolds(eye - new Vector3(0f, Camera.EyeHeight, 0f));

    /// <summary>Whether a body whose feet are at <paramref name="feet"/> (a mob's, a player's) stands in the cloud:
    /// the one reach rule <see cref="CloudReaches"/> shares.</summary>
    public bool CloudHolds(Vector3 feet)
    {
        if (!CloudActive) return false;
        var dx = feet.X - Position.X;
        var dz = feet.Z - Position.Z;
        return dx * dx + dz * dz <= WildlifeConfig.BloomBurstRadiusBlocks * WildlifeConfig.BloomBurstRadiusBlocks
               && MathF.Abs(feet.Y - Feet.Y) <= WildlifeConfig.BloomBurstHeightBlocks * 1.5f;
    }

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