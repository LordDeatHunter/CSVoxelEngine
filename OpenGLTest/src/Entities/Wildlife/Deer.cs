using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The deer: a shy grazer that never attacks. It watches a player inside
/// <see cref="WildlifeConfig.DeerAlertRangeBlocks"/> (raising its head once, its <c>alert</c> clip), bolts
/// when one comes within <see cref="WildlifeConfig.DeerFleeRangeBlocks"/> or hurts it, and runs for
/// <see cref="WildlifeConfig.DeerFleeSeconds"/> at <see cref="WildlifeConfig.DeerRunSpeed"/>, veering off
/// the straight line away so a herd scatters. Left alone it wanders and grazes. Main thread only.
/// </summary>
public sealed class Deer : Animal
{
    /// <summary>The entity file name (<c>assets/data/entities/deer.json</c>).</summary>
    public const string EntityName = "deer";

    public const string GrazeClip = "graze";
    public const string AlertClip = "alert";
    public const string GallopClip = "run";

    // The counted action clips (RealmMob.StartAttack), which a client's mirror replays by this index
    private const int GrazeAction = 0;
    private const int AlertAction = 1;

    private float _fleeSeconds;
    private float _veer;
    private bool _alerted;
    private float _grazeIn = Roll(WildlifeConfig.DeerGrazeMinSeconds, WildlifeConfig.DeerGrazeMaxSeconds);

    public Deer(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, WildlifeConfig.DeerWalkSpeed, spawnIn)
    {
    }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Deer;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.DeerHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.DeerHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.DeerShoveSpeed;

    /// <inheritdoc />
    protected override float WalkSpeed => WildlifeConfig.DeerWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => WildlifeConfig.DeerJumpSpeed;

    /// <inheritdoc />
    protected override float GroundSpeedFactor =>
        IsFleeing ? WildlifeConfig.DeerRunSpeed / WildlifeConfig.DeerWalkSpeed : 1f;

    /// <inheritdoc />
    protected override string RunClip => GallopClip;

    /// <inheritdoc />
    protected override float RunSpeed => WildlifeConfig.DeerRunSpeed;

    /// <inheritdoc />
    public override string AttackClip => GrazeClip;

    /// <inheritdoc />
    protected override int AttackVariants => 2;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant == AlertAction ? AlertClip : GrazeClip;

    // Both of its clips are habits it drops the moment it has to run
    /// <inheritdoc />
    protected override bool IdleActionPlaying => Animation.IsActionPlaying;

    /// <inheritdoc />
    protected override bool ActsThroughAction => true;

    /// <summary>Whether the deer is running away.</summary>
    public bool IsFleeing => _fleeSeconds > 0f;

    /// <inheritdoc />
    protected override bool FacesTarget => !IsFleeing;

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        IsFleeing || NearestThreatSquared(targets)
        <= WildlifeConfig.DeerAlertRangeBlocks * WildlifeConfig.DeerAlertRangeBlocks;

    /// <summary>Throws, naming the model file, unless the model is a four-legged rig on the humanoid group
    /// names with idle, walk, run, graze and alert clips.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireParts(type, "Deer", EntityContract.HumanoidParts);
        foreach (var clip in (string[])[EntityAnimator.IdleClip, EntityAnimator.WalkClip, GallopClip, GrazeClip, AlertClip])
            EntityContract.RequireClip(type, "Deer", clip);
    }

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        base.TakeDamage(amount);
        if (amount > 0) Bolt();
    }

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _fleeSeconds = MathF.Max(0f, _fleeSeconds - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (dist <= WildlifeConfig.DeerFleeRangeBlocks) Bolt();
        if (IsFleeing)
        {
            // Away from the player, turned by this flight's veer
            var (sin, cos) = MathF.SinCos(_veer);
            return new Vector3(-(toward.X * cos - toward.Z * sin), 0f, -(toward.X * sin + toward.Z * cos));
        }

        // Inside the alert range: it stands and watches, lifting its head the first time
        if (!_alerted)
        {
            _alerted = true;
            StartAttack(AlertAction);
        }
        return Vector3.Zero;
    }

    /// <inheritdoc />
    protected override void IdleTick(float deltaTime)
    {
        _alerted = false;
        _grazeIn -= deltaTime;
        if (_grazeIn > 0f || !IsGrounded || Animation.IsActionPlaying) return;
        _grazeIn = Roll(WildlifeConfig.DeerGrazeMinSeconds, WildlifeConfig.DeerGrazeMaxSeconds);
        StartAttack(GrazeAction);
    }

    // Starts (or keeps up) a flight; a new one picks its own veer and drops whatever clip was playing
    private void Bolt()
    {
        if (!IsFleeing)
        {
            var spread = WildlifeConfig.DeerFleeVeerDegrees * (MathF.PI / 180f);
            _veer = (Random.Shared.NextSingle() * 2f - 1f) * spread;
            Animation.CutAction();
        }
        _fleeSeconds = WildlifeConfig.DeerFleeSeconds;
    }
}
