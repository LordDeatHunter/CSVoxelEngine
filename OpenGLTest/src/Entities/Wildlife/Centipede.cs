using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The giant centipede: the jungle's long hunter. A player within <see cref="WildlifeConfig.CentipedeNoticeRangeBlocks"/>
/// is prey: it runs at them on its <c>walk</c> clip (a traveling wave over its ten leg pairs) and, within
/// <see cref="WildlifeConfig.CentipedeBiteReach"/> and off its cooldown, plays <c>bite</c>; at the clip's
/// <see cref="ImpactEvent"/> its jaws land (<see cref="TryTakeStrike"/>) and the venom takes hold. It is one
/// simulated body that plays its authored clips, drawn as a chain (<see cref="Spine"/>): the head turns and each
/// segment trails the one ahead, so it bends through a turn like a worm instead of swinging round whole. The
/// chain is a pose, not physics (nothing collides with or hits a segment), and its legs are clip-driven, not
/// solved against the ground. Main thread only.
/// </summary>
public sealed class Centipede : Animal
{
    /// <summary>The entity file name (<c>assets/data/entities/centipede.json</c>).</summary>
    public const string EntityName = "centipede";

    public const string BiteClip = "bite";

    /// <summary>Timeline event on the bite clip at which the mandibles close.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>The status effect its bite gives (<c>assets/data/effects/venom.json</c>).</summary>
    public const string VenomEffect = "venom";

    private float _biteCooldown;

    public Centipede(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, WildlifeConfig.CentipedeWalkSpeed, spawnIn)
    {
        Spine = new CentipedeSpine(CentipedeRig.For(type));
    }

    /// <summary>The body chain the parts are drawn on; it follows the body once a frame, on the simulation and on a
    /// client's mirror alike.</summary>
    public CentipedeSpine Spine { get; }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Centipede;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.CentipedeHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.CentipedeHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.CentipedeShoveSpeed;

    /// <inheritdoc />
    protected override float WalkSpeed => WildlifeConfig.CentipedeWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => WildlifeConfig.CentipedeJumpSpeed;

    /// <inheritdoc />
    protected override float TurnRate => WildlifeConfig.CentipedeTurnRate;

    // A hunter worth reading: its bar always shows
    /// <inheritdoc />
    public override bool ShowsHealthbar => true;

    /// <inheritdoc />
    public override string AttackClip => BiteClip;

    /// <summary>Whether a centipede centered at <paramref name="center"/> bites a player whose eye is at
    /// <paramref name="eye"/>.</summary>
    public static bool InBiteReach(Vector3 center, Vector3 eye) =>
        MobTargets.InMeleeReach(center, eye, WildlifeConfig.CentipedeBiteReach);

    /// <summary>Throws, naming the model file, unless the model has the head, a segment and the tail laid out as
    /// a chain (<see cref="CentipedeRig"/>), the idle and walk clips and a bite clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireParts(type, "Giant Centipede", ["Head", "Segment1", "Tail"]);
        EntityContract.RequireClip(type, "Giant Centipede", EntityAnimator.IdleClip);
        EntityContract.RequireClip(type, "Giant Centipede", EntityAnimator.WalkClip);
        EntityContract.RequireEvent(type, "Giant Centipede",
            EntityContract.RequireClip(type, "Giant Centipede", BiteClip), ImpactEvent);
        CentipedeRig.For(type);
    }

    /// <inheritdoc />
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        base.Locomote(move, target, wander, deltaTime, chunks);
        Spine.Follow(Feet, Facing);
    }

    /// <inheritdoc />
    public override void AnimateMirror(float deltaTime)
    {
        base.AnimateMirror(deltaTime);
        Spine.Follow(Feet, Facing);
    }

    /// <summary>The world matrix of rig part <paramref name="part"/>: the clips' pose on the root of the joint the
    /// part rides (<see cref="CentipedeSpine.RootOf"/>) rather than on the one body's.</summary>
    public override Matrix4x4 PartMatrix(string part, PlayerRig rig)
    {
        // Drawn before its first frame (a spawn this frame), the chain lies straight
        if (!Spine.IsLaid) Spine.Follow(Feet, Facing);
        return RigPose.PartMatrix(Animation, part, rig, Spine.RootOf(part));
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        NearestThreatSquared(targets)
        <= WildlifeConfig.CentipedeNoticeRangeBlocks * WildlifeConfig.CentipedeNoticeRangeBlocks;

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!InBiteReach(Position, target.Eye)) return toward;
        if (_biteCooldown > 0f) return Vector3.Zero;

        StartAttack();
        _biteCooldown = WildlifeConfig.CentipedeBiteCooldownSeconds;
        return Vector3.Zero;
    }

    /// <inheritdoc />
    public override bool TryTakeStrike() => Animation.ConsumeEvent(ImpactEvent);

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) => InBiteReach(Position, eye);

    /// <inheritdoc />
    public override int StrikeDamage => WildlifeConfig.CentipedeBiteDamage;

    /// <inheritdoc />
    public override float StrikeKnockbackSpeed => WildlifeConfig.CentipedeKnockbackSpeed;

    /// <inheritdoc />
    public override float StrikeKnockbackLift => WildlifeConfig.CentipedeKnockbackLift;

    /// <inheritdoc />
    public override string? StrikeEffect => VenomEffect;
}