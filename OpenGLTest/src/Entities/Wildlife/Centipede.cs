using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The giant centipede: the jungle's long hunter. A player within <see cref="WildlifeConfig.CentipedeNoticeRangeBlocks"/>
/// is prey: it runs at them on its <c>walk</c> clip (a traveling wave over its ten leg pairs) and, with their chest
/// within <see cref="WildlifeConfig.CentipedeBiteReach"/> of its head and off its cooldown, plays <c>bite</c>; at the
/// clip's <see cref="ImpactEvent"/> its jaws land (<see cref="TryTakeStrike"/>) and the venom takes hold. The one
/// simulated body is its head, which crawls over floors, walls and ceilings as the spiders do
/// (<see cref="CrawlDrive"/>: it climbs what it walks into, rounds ledges, leaps and plans a path when the chase
/// stalls, is knocked off a wall by a blow and never takes fall damage). The rest is a chain that trails the head
/// along the path it took (<see cref="Spine"/>), so the body bends through a turn and drapes up a wall segment by
/// segment; a blow lands on any segment, on the one health pool. Its legs are clip-driven, not solved against the
/// ground. Main thread only.
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

    private readonly CrawlDrive _drive;
    private float _biteCooldown;

    public Centipede(EntityType type, Vector3 position, bool spawnIn = true)
        : base(type, position, WildlifeConfig.CentipedeWalkSpeed, spawnIn)
    {
        _drive = new CrawlDrive(BodyHalfWidth, SeesTarget,
            WildlifeConfig.CentipedeWalkSpeed / WildlifeConfig.CentipedeTurnRadiusBlocks);
        Spine = new CentipedeSpine(CentipedeRig.For(type), BodyHalfWidth);
    }

    /// <summary>The body chain the parts are drawn on and a blow is tested against; it follows the head once a
    /// frame, on the simulation and on a client's mirror alike.</summary>
    public CentipedeSpine Spine { get; }

    /// <summary>The outward normal of the surface the head clings to; the world's up while it falls.</summary>
    public Vector3 SurfaceNormal => _drive.Surface.Up;

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Centipede;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.CentipedeBodySize;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.CentipedeBodySize * 0.5f;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.CentipedeShoveSpeed;

    /// <inheritdoc />
    protected override float WalkSpeed => WildlifeConfig.CentipedeWalkSpeed;

    // It climbs what it walks into instead of jumping it
    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    // It turns along the surface it is on, not about the world's up
    /// <inheritdoc />
    protected override bool FacesTarget => false;

    /// <inheritdoc />
    public override float SafeFallBlocks => float.PositiveInfinity;

    /// <inheritdoc />
    protected override float LocomotionSpeed => Velocity.Length();

    // A hunter worth reading: its bar always shows
    /// <inheritdoc />
    public override bool ShowsHealthbar => true;

    /// <inheritdoc />
    public override string AttackClip => BiteClip;

    /// <summary>The face the head holds (<see cref="CrawlDrive.SurfaceIndex"/>); with the facing, the heading's
    /// angle on it, a mirror stands the head as the server's does.</summary>
    public override byte Posture => (byte)_drive.SurfaceIndex;

    /// <inheritdoc />
    /// <remarks>The facing was set just before.</remarks>
    public override void MirrorPosture(byte posture) => _drive.Mirror(posture, Facing);

    /// <summary>Whether a centipede whose head is centered at <paramref name="center"/> bites a player whose eye is
    /// at <paramref name="eye"/>: from any side, as it attacks off walls too.</summary>
    public static bool InBiteReach(Vector3 center, Vector3 eye) =>
        CrawlerMob.ReachesChest(center, eye, WildlifeConfig.CentipedeBiteReach);

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

    /// <summary>
    /// Crawls the head one frame (<see cref="CrawlDrive.Step"/>): toward the chased target's chest or the wander
    /// point along whatever surface it holds, or holding still for no move; the body then follows.
    /// </summary>
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        var position = Position;
        var velocity = Velocity;
        var order = new CrawlOrder(move, target, wander, IsDying, WildlifeConfig.CentipedeWalkSpeed * MoveFactor,
            WildlifeConfig.CentipedeBiteReach, Facing);
        var impact = _drive.Step(ref position, ref velocity, order, deltaTime, chunks);
        Position = position;
        SetVelocity(velocity);
        if (impact > 0f) NoteLanding(impact, BodyMotion.Gravity, _drive.Surface.InWater);
        IsGrounded = _drive.Surface.OnFloor;
        Facing = _drive.Facing;
        Animation.Update(deltaTime, LocomotionSpeed);
        FollowHead();
    }

    /// <inheritdoc />
    public override void AnimateMirror(float deltaTime)
    {
        base.AnimateMirror(deltaTime);
        FollowHead();
    }

    private void FollowHead() => Spine.Follow(Position, _drive.Surface.Up, _drive.Surface.Heading);

    /// <inheritdoc />
    /// <remarks>On a floor it slides like any walker. On a wall or a ceiling the blow knocks it loose: it lets go
    /// and falls with the shove.</remarks>
    public override void Shove(Vector3 direction, float speed)
    {
        var velocity = Velocity;
        if (_drive.KnockLoose(direction, speed, ref velocity)) SetVelocity(velocity);
        else base.Shove(direction, speed);
    }

    /// <summary>The world matrix of rig part <paramref name="part"/>: the clips' pose on the root of the joint the
    /// part rides (<see cref="CentipedeSpine.RootOf"/>) rather than on the one body's.</summary>
    public override Matrix4x4 PartMatrix(string part, PlayerRig rig)
    {
        // Drawn before its first frame (a spawn this frame), the chain lies straight
        if (!Spine.IsLaid) FollowHead();
        return RigPose.PartMatrix(Animation, part, rig, Spine.RootOf(part));
    }

    /// <inheritdoc />
    /// <remarks>It stands on any surface, so it has no lean: the spawn pop and the death's shrink, about the head.</remarks>
    public override Matrix4x4 BodyEffect(Vector3 feet) =>
        CrawlerMob.PopAndRoll(Position, _drive.Surface.Heading, SpawnProgress, DeathProgress, deathRoll: 0f);

    /// <inheritdoc />
    /// <remarks>Every joint of the chain is a cube of the head's size.</remarks>
    public override bool OverlapsBox(Vector3 center, float halfExtent)
    {
        if (!Spine.IsLaid) return base.OverlapsBox(center, halfExtent);
        var reach = halfExtent + BodyHalfWidth + 0.05f;
        for (var i = 0; i < Spine.Count; i++)
        {
            var d = center - Spine[i];
            if (MathF.Abs(d.X) <= reach && MathF.Abs(d.Y) <= reach && MathF.Abs(d.Z) <= reach) return true;
        }
        return false;
    }

    /// <inheritdoc />
    /// <remarks>The joint of the chain nearest <paramref name="from"/>.</remarks>
    public override Vector3? AimPointFrom(Vector3 from)
    {
        if (!Spine.IsLaid) return Position;
        var nearest = Spine[0];
        for (var i = 1; i < Spine.Count; i++)
        {
            if (Vector3.DistanceSquared(from, Spine[i]) < Vector3.DistanceSquared(from, nearest)) nearest = Spine[i];
        }
        return nearest;
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        NearestThreatSquared(targets)
        <= WildlifeConfig.CentipedeNoticeRangeBlocks * WildlifeConfig.CentipedeNoticeRangeBlocks;

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);
        _drive.TickCooldowns(scaledDelta);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (!InBiteReach(Position, target.Eye))
            return CrawlDrive.Approach(Position, toward, target.Eye, WildlifeConfig.CentipedeBiteReach);
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
