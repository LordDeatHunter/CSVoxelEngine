using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Spiders;

/// <summary>
/// The giant spider, and its oversized broodmother (a miniboss): a cave mob that walks on every surface. Its
/// body clings to floors, walls and ceilings through a <see cref="SurfaceCrawler"/>, so it climbs straight at its
/// target, rounds ledges and drops off a ceiling onto whoever stands under it; in reach and off its cooldown it
/// plays its bite, which lands at the clip's <see cref="ImpactEvent"/> (<see cref="TryTakeBiteImpact"/>) with
/// venom. Its eight legs are not keyframed: <see cref="SpiderLegs"/> plants each foot on the blocks around it and
/// solves the leg to reach it, on the eye's side only (<see cref="AnimateCosmetics"/>). A blow knocks it off a wall
/// or a ceiling, and it never takes fall damage. Main thread only.
/// </summary>
public sealed class Spider : CaveMob
{
    /// <summary>The entity file names (<c>assets/data/entities/spider.json</c>, <c>spider_broodmother.json</c>).</summary>
    public const string EntityName = "spider";
    public const string BroodmotherEntityName = "spider_broodmother";

    public const string BiteClip = "bite";

    /// <summary>Timeline event on the bite clip at which the fangs close.</summary>
    public const string ImpactEvent = "impact";

    // The death: the body rolls onto its back over the first share of it, then shrinks away over the last
    private const float DeathRollShare = 0.5f;
    private const float DeathShrinkFrom = 0.75f;

    private readonly SurfaceCrawler _crawler = new();
    private readonly Tuning _tuning;
    private float _biteCooldown;
    private bool _acquired;
    private Quaternion _orientation = Quaternion.Identity;
    private bool _oriented;

    private readonly record struct Tuning(float BodySize, float CrawlSpeed, float BiteReach, int BiteDamage,
        float BiteCooldown, float KnockbackSpeed, float KnockbackLift, float VenomScale, float ShoveSpeed);

    private static readonly Tuning Regular = new(SpiderConfig.SpiderBodySize, SpiderConfig.SpiderCrawlSpeed,
        SpiderConfig.SpiderBiteReach, SpiderConfig.SpiderBiteDamage, SpiderConfig.SpiderBiteCooldownSeconds,
        SpiderConfig.SpiderKnockbackSpeed, SpiderConfig.SpiderKnockbackLift, SpiderConfig.SpiderVenomScale,
        SpiderConfig.SpiderShoveSpeed);

    private static readonly Tuning Broodmother = new(SpiderConfig.BroodmotherBodySize,
        SpiderConfig.BroodmotherCrawlSpeed, SpiderConfig.BroodmotherBiteReach, SpiderConfig.BroodmotherBiteDamage,
        SpiderConfig.BroodmotherBiteCooldownSeconds, SpiderConfig.BroodmotherKnockbackSpeed,
        SpiderConfig.BroodmotherKnockbackLift, SpiderConfig.BroodmotherVenomScale, SpiderConfig.BroodmotherShoveSpeed);

    public Spider(EntityType type, Vector3 position, int tier, bool broodmother, bool spawnIn = true)
        : base(type, position, tier, (broodmother ? Broodmother : Regular).CrawlSpeed, spawnIn)
    {
        IsBroodmother = broodmother;
        _tuning = broodmother ? Broodmother : Regular;
        Legs = new SpiderLegs(SpiderRig.For(type));
    }

    /// <summary>Whether this is the broodmother, the spider den's miniboss.</summary>
    public bool IsBroodmother { get; }

    /// <summary>The procedural legs; posed only where the spider is drawn.</summary>
    public SpiderLegs Legs { get; }

    /// <summary>The edge of the named kind's cube body (where a scenario or a spawn puts its center above the
    /// floor), or 0 for a kind that is no spider.</summary>
    public static float BodySizeOf(string kind) =>
        string.Equals(kind, EntityName, StringComparison.OrdinalIgnoreCase) ? Regular.BodySize
        : string.Equals(kind, BroodmotherEntityName, StringComparison.OrdinalIgnoreCase) ? Broodmother.BodySize
        : 0f;

    /// <inheritdoc />
    public override float BodyHeight => _tuning.BodySize;

    /// <inheritdoc />
    public override float BodyHalfWidth => _tuning.BodySize * 0.5f;

    /// <inheritdoc />
    protected override float WalkSpeed => _tuning.CrawlSpeed;

    // A spider climbs what it walks into instead of jumping it
    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    /// <inheritdoc />
    public override string AttackClip => BiteClip;

    // It turns along the surface it is on, not about the world's up
    /// <inheritdoc />
    protected override bool FacesTarget => false;

    /// <inheritdoc />
    public override float SafeFallBlocks => float.PositiveInfinity;

    /// <inheritdoc />
    protected override float LocomotionSpeed => Velocity.Length();

    /// <summary>The damage one bite deals at this spider's tier.</summary>
    public int BiteDamage => CaveDifficulty.Scale(_tuning.BiteDamage, Tier);

    /// <summary>How hard a bite throws its target back, and up.</summary>
    public float KnockbackSpeed => _tuning.KnockbackSpeed;

    /// <inheritdoc cref="KnockbackSpeed"/>
    public float KnockbackLift => _tuning.KnockbackLift;

    /// <summary>The share of the venom effect's duration a bite applies.</summary>
    public float VenomScale => _tuning.VenomScale;

    /// <summary>How fast a player walking into it on a floor shoves it aside.</summary>
    public float ShoveSpeed => _tuning.ShoveSpeed;

    /// <summary>The outward normal of the surface the spider clings to; the world's up while it falls.</summary>
    public Vector3 SurfaceNormal => _crawler.Up;

    /// <summary>Whether the spider holds a surface (false while it falls or floats).</summary>
    public bool IsClinging => _crawler.Attached;

    /// <summary>The surface as a server sends it: <see cref="CrawlFrame.Airborne"/>, or the held face's index. With
    /// <see cref="Mob.Facing"/>, the heading's angle on that face, it is the whole stance.</summary>
    public int SurfaceIndex => _crawler.Attached ? CrawlFrame.IndexOf(_crawler.Normal) : CrawlFrame.Airborne;

    /// <summary>What a server sends in the entry's item field: the depth tier in the low byte (as every cave mob),
    /// the <see cref="SurfaceIndex"/> above it.</summary>
    public ushort PackStance() => (ushort)(Tier | (SurfaceIndex << 8));

    /// <summary>The surface index <see cref="PackStance"/> packed into <paramref name="packed"/>.</summary>
    public static int SurfaceOf(ushort packed) => packed >> 8;

    /// <summary>A client's mirror takes the surface a snapshot reports; its facing was set just before.</summary>
    public void MirrorSurface(int index)
    {
        var normal = CrawlFrame.NormalOf(index);
        _crawler.Mirror(normal, CrawlFrame.HeadingOf(normal == Vector3.Zero ? Vector3.UnitY : normal, Facing));
        _acquired = true;
    }

    /// <summary>Whether a spider of this kind centered at <paramref name="center"/> can sink its fangs into a
    /// player whose eye is at <paramref name="eye"/>: the chest within its bite reach, from any side.</summary>
    public bool InBiteReach(Vector3 center, Vector3 eye) =>
        Vector3.DistanceSquared(center, ChestOf(eye)) <= _tuning.BiteReach * _tuning.BiteReach;

    /// <summary>Throws, naming the model file, unless the model has the body groups, the eight two-segment legs
    /// (<see cref="SpiderRig"/>), idle and walk clips and a bite clip carrying the impact event.</summary>
    public static void ValidateContent(EntityType type)
    {
        const string kind = "Spider";
        EntityContract.RequireParts(type, kind, ["Body", "Head", "Abdomen"]);
        SpiderRig.For(type);
        EntityContract.RequireClip(type, kind, EntityAnimator.IdleClip);
        EntityContract.RequireClip(type, kind, EntityAnimator.WalkClip);
        EntityContract.RequireEvent(type, kind, EntityContract.RequireClip(type, kind, BiteClip), ImpactEvent);
    }

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta) =>
        _biteCooldown = MathF.Max(0f, _biteCooldown - scaledDelta);

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (InBiteReach(Position, target.Eye))
        {
            if (_biteCooldown > 0f) return Vector3.Zero;
            StartAttack();
            _biteCooldown = _tuning.BiteCooldown;
            return Vector3.Zero;
        }

        // Close enough already (a target on the far side of a thin wall): it waits rather than pressing into it
        var standoff = _tuning.BiteReach * SpiderConfig.StandoffShare;
        if (Vector3.DistanceSquared(Position, ChestOf(target.Eye)) <= standoff * standoff) return Vector3.Zero;
        // The crawl aims at the target itself (Locomote); this only says "move"
        return toward == Vector3.Zero ? Vector3.UnitY : toward;
    }

    /// <summary>True once per bite, at the bite clip's impact event; the manager decides who is still in reach.</summary>
    public bool TryTakeBiteImpact() => Animation.ConsumeEvent(ImpactEvent);

    /// <summary>
    /// Crawls one frame: toward the chased target's chest or the wander point along whatever surface it holds, or
    /// holding still for no move. A dying spider lets go of a wall or a ceiling; one on a ceiling over its target
    /// lets go to land on it.
    /// </summary>
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        var half = BodyHalfWidth;
        if (!_acquired)
        {
            _acquired = true;
            _crawler.Acquire(Position, half, Facing, chunks);
        }

        Vector3? goal = null;
        var speed = _tuning.CrawlSpeed * MoveFactor;
        if (IsDying)
        {
            if (_crawler.Attached && !_crawler.OnFloor) _crawler.Detach();
        }
        else if (move != Vector3.Zero)
        {
            var to = (target != null ? ChestOf(target.Eye) : wander ?? Position) - Position;
            if (to.LengthSquared() > 1e-6f) goal = Vector3.Normalize(to);
            // A stroll hands in a fraction of a unit move
            if (target == null) speed *= MathF.Min(1f, move.Length());
            if (target != null && _crawler.OnCeiling && to.Y < 0f
                && to.X * to.X + to.Z * to.Z < SpiderConfig.CeilingDropRadiusBlocks * SpiderConfig.CeilingDropRadiusBlocks)
                _crawler.Detach();
        }

        var position = Position;
        var velocity = Velocity;
        var impact = _crawler.Step(ref position, ref velocity, goal, speed, half, deltaTime, chunks, mayGrab: !IsDying);
        Position = position;
        SetVelocity(velocity);
        if (impact > 0f) NoteLanding(impact, BodyMotion.Gravity, _crawler.InWater);
        IsGrounded = _crawler.OnFloor;
        Facing = CrawlFrame.AngleOf(_crawler.Up, _crawler.Heading);
        Animation.Update(deltaTime, LocomotionSpeed);
    }

    /// <inheritdoc />
    /// <remarks>On a floor it slides like any walker. On a wall or a ceiling the blow knocks it loose: it lets go
    /// and falls with the shove.</remarks>
    public override void Shove(Vector3 direction, float speed)
    {
        if (!_crawler.Attached || _crawler.OnFloor)
        {
            base.Shove(direction, speed);
            return;
        }

        if (direction.LengthSquared() < 1e-6f) return;
        _crawler.Detach();
        SetVelocity(Vector3.Normalize(direction) * speed);
    }

    /// <summary>
    /// Turns the drawn body toward the surface it stands on (the held face, leaned into the plane its feet have
    /// found, so it tilts over steps and corners instead of snapping) and walks the legs. Run by the world's
    /// effects pass wherever the spider is drawn; a null <paramref name="chunks"/> (a model viewer) walks the legs
    /// on the model's own ground plane.
    /// </summary>
    public override void AnimateCosmetics(float deltaTime, ChunkManager? chunks)
    {
        var up = _crawler.Up;
        if (_crawler.Attached && !IsDying)
            up = Vector3.Normalize(Vector3.Lerp(up, Legs.FeetNormal(up), SpiderConfig.FeetNormalWeight));
        var target = CrawlFrame.Orientation(up, _crawler.Heading);
        _orientation = _oriented
            ? Quaternion.Slerp(_orientation, target, 1f - MathF.Exp(-SpiderConfig.BodyTurnRate * deltaTime))
            : target;
        _oriented = true;
        Legs.Update(deltaTime, Root, Velocity, chunks);
    }

    /// <summary>Where the model stands in the world: its ground plane half a body under the center along the drawn
    /// up, turned by the drawn orientation.</summary>
    public Matrix4x4 Root
    {
        get
        {
            var up = Vector3.Transform(Vector3.UnitY, _orientation);
            return Matrix4x4.CreateFromQuaternion(_orientation)
                   * Matrix4x4.CreateTranslation(Position - up * BodyHalfWidth);
        }
    }

    /// <inheritdoc />
    public override Matrix4x4 PartMatrix(string part, PlayerRig rig) =>
        RigPose.PartMatrix(new SpiderPose(Animation, Legs), part, rig, Root);

    /// <inheritdoc />
    /// <remarks>A spider has no lean: its pop-in, and its death, rolling onto its back about its own length before
    /// it shrinks away, both about the body center.</remarks>
    public override Matrix4x4 BodyEffect(Vector3 feet)
    {
        var scale = MathUtils.EaseOutBack(SpawnProgress)
                    * (1f - MathUtils.EaseInCubic(MathUtils.InverseLerp(DeathShrinkFrom, 1f, DeathProgress)));
        var roll = MathF.PI * MathUtils.SmoothStep(MathUtils.InverseLerp(0f, DeathRollShare, DeathProgress));
        var forward = Vector3.Transform(-Vector3.UnitZ, _orientation);
        return Matrix4x4.CreateTranslation(-Position)
               * Matrix4x4.CreateScale(MathF.Max(scale, 0.001f))
               * Matrix4x4.CreateFromAxisAngle(forward, roll)
               * Matrix4x4.CreateTranslation(Position);
    }
}
