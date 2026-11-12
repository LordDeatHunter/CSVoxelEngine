using System;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// A cave mob that walks on every surface on solved legs (the <see cref="Spider"/>, the <see cref="Scorpion"/>).
/// Its body clings to floors, walls and ceilings through a <see cref="SurfaceCrawler"/>, so it climbs straight at
/// its target, rounds ledges and drops off a ceiling onto whoever stands under it. Its eight legs are not
/// keyframed: <see cref="CrawlerLegs"/> plants each foot on the blocks around it and solves the leg to reach it,
/// on the eye's side only (<see cref="AnimateCosmetics"/>). A blow knocks it off a wall or a ceiling, and it never
/// takes fall damage. A kind adds its body size, speed and attacks. Main thread only.
/// </summary>
public abstract class CrawlerMob : CaveMob
{
    // The death: the body may roll over the first share of it, then shrinks away over the last
    private const float DeathRollShare = 0.5f;
    private const float DeathShrinkFrom = 0.75f;

    private readonly SurfaceCrawler _crawler = new();
    private readonly CrawlerNavigator _navigator = new();
    private readonly AimTracker _aim = new();
    private readonly float _bodySize;
    private readonly float _crawlSpeed;
    private float _leapCooldown;
    private bool _acquired;
    private Quaternion _orientation = Quaternion.Identity;
    private bool _oriented;

    /// <param name="bodySize">The edge of the cube body, the same on every surface.</param>
    /// <param name="crawlSpeed">Its speed along a surface, which its walk clip is authored at.</param>
    protected CrawlerMob(EntityType type, Vector3 position, int tier, float bodySize, float crawlSpeed, bool spawnIn)
        : base(type, position, tier, crawlSpeed, spawnIn)
    {
        _bodySize = bodySize;
        _crawlSpeed = crawlSpeed;
        Legs = new CrawlerLegs(CrawlerRig.For(type));
    }

    /// <summary>The procedural legs; posed only where the mob is drawn.</summary>
    public CrawlerLegs Legs { get; }

    /// <inheritdoc />
    public override float BodyHeight => _bodySize;

    /// <inheritdoc />
    public override float BodyHalfWidth => _bodySize * 0.5f;

    /// <inheritdoc />
    protected override float WalkSpeed => _crawlSpeed;

    // A crawler climbs what it walks into instead of jumping it
    /// <inheritdoc />
    protected override float JumpSpeed => 0f;

    // It turns along the surface it is on, not about the world's up
    /// <inheritdoc />
    protected override bool FacesTarget => false;

    /// <inheritdoc />
    public override float SafeFallBlocks => float.PositiveInfinity;

    /// <inheritdoc />
    public override string? HitSoundGroup => SfxLibrary.BugSquishGroup;

    /// <inheritdoc />
    public override string? DeathSoundGroup => SfxLibrary.BugSquishGroup;

    /// <inheritdoc />
    protected override float LocomotionSpeed => Velocity.Length();

    /// <summary>How fast a player walking into it on a floor shoves it aside.</summary>
    public abstract float ShoveSpeed { get; }

    /// <summary>How far the dying body rolls about its own length, in radians: none by default; a spider ends on
    /// its back.</summary>
    protected virtual float DeathRollRadians => 0f;

    /// <summary>The outward normal of the surface the crawler clings to; the world's up while it falls.</summary>
    public Vector3 SurfaceNormal => _crawler.Up;

    /// <summary>Whether the crawler holds a surface (false while it falls or floats).</summary>
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

    /// <summary>Whether a body centered at <paramref name="center"/> has the chest of a player whose eye is at
    /// <paramref name="eye"/> within <paramref name="reach"/>, from any side (a crawler attacks off walls too).</summary>
    public static bool ReachesChest(Vector3 center, Vector3 eye, float reach) =>
        Vector3.DistanceSquared(center, ChestOf(eye)) <= reach * reach;

    /// <summary>
    /// The crawler's shared contract: throws, naming the model file, unless the model has the groups
    /// <paramref name="parts"/>, the eight two-segment legs (<see cref="CrawlerRig"/>), idle and walk clips, and
    /// each of <paramref name="attackClips"/> carrying <paramref name="attackEvent"/>.
    /// </summary>
    protected static void RequireCrawler(EntityType type, string kind, string[] parts, string[] attackClips,
        string attackEvent)
    {
        EntityContract.RequireParts(type, kind, parts);
        CrawlerRig.For(type);
        EntityContract.RequireClip(type, kind, EntityAnimator.IdleClip);
        EntityContract.RequireClip(type, kind, EntityAnimator.WalkClip);
        foreach (var clip in attackClips)
            EntityContract.RequireEvent(type, kind, EntityContract.RequireClip(type, kind, clip), attackEvent);
    }

    /// <summary>Where a ranged attack leaves the body: a little off the held surface over the body's center.</summary>
    public Vector3 ShotOrigin => Position + _crawler.Up * (BodyHalfWidth * 0.5f);

    /// <inheritdoc />
    /// <remarks>Also counts the leap down and keeps a ranged attack's aim on its target through the wind-up.</remarks>
    protected sealed override void TickCooldowns(float scaledDelta)
    {
        _leapCooldown = MathF.Max(0f, _leapCooldown - scaledDelta);
        _aim.Track(scaledDelta, Animation.IsActionPlaying);
        TickAttackCooldowns(scaledDelta);
    }

    /// <summary>Counts the kind's own attack cooldowns down (see <see cref="RealmMob.TickCooldowns"/>).</summary>
    protected abstract void TickAttackCooldowns(float scaledDelta);

    /// <summary>
    /// Whether a ranged attack may start at <paramref name="target"/> now: the crawler holds a surface and the
    /// target's chest is <paramref name="minRange"/>..<paramref name="maxRange"/> from <see cref="ShotOrigin"/> in a
    /// clear line. When so the aim locks onto the target (the caller starts the clip, and
    /// <see cref="ReleaseShot"/> launches at its release).
    /// </summary>
    protected bool TryAimShot(IMobTarget target, ChunkManager chunks, float minRange, float maxRange)
    {
        if (!IsClinging) return false;
        var range = Vector3.Distance(ShotOrigin, ChestOf(target.Eye));
        if (range < minRange || range > maxRange || !SeesTarget(ShotOrigin, target, chunks)) return false;
        _aim.Lock(target, chunks);
        return true;
    }

    /// <summary>
    /// A ranged attack's release: where the shot leaves the body and the velocity of <paramref name="speed"/> that
    /// meets the target locked by <see cref="TryAimShot"/> where it will be (<see cref="AimTracker"/>; a
    /// maximum-range toss when out of reach).
    /// </summary>
    public void ReleaseShot(float speed, out Vector3 origin, out Vector3 velocity)
    {
        origin = ShotOrigin;
        _aim.Solve(origin, speed, ThrownProjectile.Gravity, out velocity);
    }

    /// <summary>A cooldown of <paramref name="seconds"/> give or take up to <paramref name="jitter"/>, so a pack's
    /// shots fall out of step.</summary>
    protected static float Jittered(float seconds, float jitter) =>
        seconds + (Random.Shared.NextSingle() - 0.5f) * 2f * jitter;

    /// <summary>
    /// What <see cref="RealmMob.Decide"/> answers for a target still out of attack <paramref name="reach"/>: zero
    /// (wait) once inside the standoff share of it, which is where a target behind a thin wall leaves the crawler,
    /// else a unit move. The crawl itself aims at the target (<see cref="Locomote"/>); this only says "move".
    /// </summary>
    protected Vector3 Approach(Vector3 toward, IMobTarget target, float reach)
    {
        var standoff = reach * CrawlerConfig.StandoffShare;
        if (ReachesChest(Position, target.Eye, standoff)) return Vector3.Zero;
        return toward == Vector3.Zero ? Vector3.UnitY : toward;
    }

    /// <summary>
    /// Crawls one frame: toward the chased target's chest or the wander point along whatever surface it holds, or
    /// holding still for no move. A chase that stalls is helped along (<see cref="CrawlerNavigator"/>): a leap at
    /// the target when one is in reach, else a path over the block faces. A dying crawler lets go of a wall or a
    /// ceiling; one on a ceiling over its target lets go to land on it.
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
        var speed = _crawlSpeed * MoveFactor;
        if (IsDying)
        {
            if (_crawler.Attached && !_crawler.OnFloor) _crawler.Detach();
        }
        else if (move != Vector3.Zero)
        {
            var to = (target != null ? Chase(target, deltaTime, chunks) : wander ?? Position) - Position;
            if (to.LengthSquared() > 1e-6f) goal = Vector3.Normalize(to);
            // A stroll hands in a fraction of a unit move
            if (target == null) speed *= MathF.Min(1f, move.Length());
            var below = target != null ? ChestOf(target.Eye) - Position : Vector3.Zero;
            if (_crawler.OnCeiling && below.Y < 0f
                && below.X * below.X + below.Z * below.Z < CrawlerConfig.CeilingDropRadiusBlocks * CrawlerConfig.CeilingDropRadiusBlocks)
                _crawler.Detach();
        }

        if (target == null || move == Vector3.Zero) _navigator.Reset();

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

    // The point the chase aims at this frame: the target's chest, or the next foothold of a path round what keeps it
    // from closing in. A stalled chase leaps when it can and plans a path when it cannot.
    private Vector3 Chase(IMobTarget target, float deltaTime, ChunkManager chunks)
    {
        var chest = ChestOf(target.Eye);
        var aim = _navigator.Aim(Position, BodyHalfWidth, chest, _crawler, deltaTime, out var stalled, out var letGo);
        if (letGo) _crawler.Detach();
        if (stalled && !TryLeap(target, chunks)
            && _navigator.Plan(Position, BodyHalfWidth, chest, MeleeReach, _crawler, chunks))
            aim = _navigator.Aim(Position, BodyHalfWidth, chest, _crawler, 0f, out _, out _);
        return aim;
    }

    // A leap onto the target's feet: off its cooldown, from a held surface and away from it, the feet within leaping
    // distance in a clear line, on an arc no faster than a leap may launch
    private bool TryLeap(IMobTarget target, ChunkManager chunks)
    {
        if (_leapCooldown > 0f || !_crawler.Attached) return false;
        var landing = target.Eye - Vector3.UnitY * (Camera.EyeHeight - BodyHalfWidth);
        var distance = Vector3.Distance(Position, landing);
        if (distance < CrawlerConfig.LeapMinBlocks || distance > CrawlerConfig.LeapMaxBlocks) return false;

        var velocity = SurfaceCrawler.LeapVelocity(Position, landing, CrawlerConfig.LeapHorizontalSpeed);
        if (velocity.LengthSquared() > CrawlerConfig.LeapMaxSpeed * CrawlerConfig.LeapMaxSpeed
            || Vector3.Dot(velocity, _crawler.Normal) <= 0f || !SeesTarget(Position, target, chunks))
            return false;

        _crawler.Detach();
        SetVelocity(velocity);
        _leapCooldown = CrawlerConfig.LeapCooldownSeconds;
        return true;
    }

    /// <summary>How far from the target's chest the kind's melee lands, where a path may end.</summary>
    protected abstract float MeleeReach { get; }

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
    /// effects pass wherever the crawler is drawn; a null <paramref name="chunks"/> (a model viewer) walks the legs
    /// on the model's own ground plane.
    /// </summary>
    public override void AnimateCosmetics(float deltaTime, ChunkManager? chunks)
    {
        var up = _crawler.Up;
        if (_crawler.Attached && !IsDying)
            up = Vector3.Normalize(Vector3.Lerp(up, Legs.FeetNormal(up), CrawlerConfig.FeetNormalWeight));
        var target = CrawlFrame.Orientation(up, _crawler.Heading);
        _orientation = _oriented
            ? Quaternion.Slerp(_orientation, target, 1f - MathF.Exp(-CrawlerConfig.BodyTurnRate * deltaTime))
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
        RigPose.PartMatrix(new CrawlerPose(Animation, Legs), part, rig, Root);

    /// <inheritdoc />
    /// <remarks>A crawler has no lean: its pop-in, and its death (the kind's roll about its own length,
    /// <see cref="DeathRollRadians"/>, before it shrinks away), both about the body center.</remarks>
    public override Matrix4x4 BodyEffect(Vector3 feet)
    {
        var scale = MathUtils.EaseOutBack(SpawnProgress)
                    * (1f - MathUtils.EaseInCubic(MathUtils.InverseLerp(DeathShrinkFrom, 1f, DeathProgress)));
        var roll = DeathRollRadians * MathUtils.SmoothStep(MathUtils.InverseLerp(0f, DeathRollShare, DeathProgress));
        var forward = Vector3.Transform(-Vector3.UnitZ, _orientation);
        return Matrix4x4.CreateTranslation(-Position)
               * Matrix4x4.CreateScale(MathF.Max(scale, 0.001f))
               * Matrix4x4.CreateFromAxisAngle(forward, roll)
               * Matrix4x4.CreateTranslation(Position);
    }
}
