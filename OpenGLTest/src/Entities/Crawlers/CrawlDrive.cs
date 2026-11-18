using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// The crawl of a mob that walks on every surface (the <see cref="CrawlerMob"/>s, the giant centipede): its
/// <see cref="SurfaceCrawler"/> body aimed each frame at the chased target's chest or the wander point, helped
/// along when the chase stalls (<see cref="CrawlerNavigator"/>: a leap at the target when one is in reach, else a
/// path over the block faces). A dying crawler lets go of a wall or a ceiling; one on a ceiling over its target
/// lets go to land on it; a blow knocks it off whatever is not a floor. The owner keeps the position and the
/// velocity and hands them in by reference. Main thread only.
/// </summary>
/// <param name="halfExtent">Half the edge of the cube body.</param>
/// <param name="seesTarget">Whether the line from a point to a target's chest is clear (the owner's own, rationed
/// line-of-sight check), asked before a leap.</param>
/// <param name="maxTurnSpeed">The fastest the heading turns on a face, in radians a second (a long body that must
/// sweep round); unlimited by default.</param>
public sealed class CrawlDrive(float halfExtent, Func<Vector3, IMobTarget, ChunkManager, bool> seesTarget,
    float maxTurnSpeed = float.PositiveInfinity)
{
    private readonly CrawlerNavigator _navigator = new();
    private float _leapCooldown;
    private bool _acquired;

    /// <summary>The body: the face it holds and its heading there.</summary>
    public SurfaceCrawler Surface { get; } = new() { MaxTurnSpeed = maxTurnSpeed };

    /// <summary>The surface as a server sends it: <see cref="CrawlFrame.Airborne"/>, or the held face's index. With
    /// <see cref="Facing"/> it is the whole stance.</summary>
    public int SurfaceIndex => Surface.Attached ? CrawlFrame.IndexOf(Surface.Normal) : CrawlFrame.Airborne;

    /// <summary>The heading's angle on the held face, a mob's <see cref="Mob.Facing"/> (on a floor the usual yaw).</summary>
    public float Facing => CrawlFrame.AngleOf(Surface.Up, Surface.Heading);

    /// <summary>
    /// What a crawler's brain answers for a target still out of attack <paramref name="reach"/>: zero (wait) once
    /// the body at <paramref name="position"/> is inside the standoff share of it, which is where a target behind a
    /// thin wall leaves the crawler, else a unit move. The crawl itself aims at the target (<see cref="Step"/>);
    /// this only says "move".
    /// </summary>
    public static Vector3 Approach(Vector3 position, Vector3 toward, Vector3 eye, float reach)
    {
        if (CrawlerMob.ReachesChest(position, eye, reach * CrawlerConfig.StandoffShare)) return Vector3.Zero;
        return toward == Vector3.Zero ? Vector3.UnitY : toward;
    }

    /// <summary>Counts the leap's cooldown down by <paramref name="scaledDelta"/> (the frame time already slowed
    /// by a status effect).</summary>
    public void TickCooldowns(float scaledDelta) => _leapCooldown = MathF.Max(0f, _leapCooldown - scaledDelta);

    /// <summary>A client's mirror takes the surface a snapshot reports (<see cref="SurfaceIndex"/>) and the
    /// heading's angle on it.</summary>
    public void Mirror(int index, float facing)
    {
        var normal = CrawlFrame.NormalOf(index);
        Surface.Mirror(normal, CrawlFrame.HeadingOf(normal == Vector3.Zero ? Vector3.UnitY : normal, facing));
        _acquired = true;
    }

    /// <summary>
    /// Crawls one frame: toward the chased target's chest or the wander point along whatever surface the body
    /// holds, or holding still for no move. The first step takes hold of the nearest surface. Returns the speed of
    /// a landing made this frame (0 for none).
    /// </summary>
    public float Step(ref Vector3 position, ref Vector3 velocity, in CrawlOrder order, float deltaTime,
        ChunkManager chunks)
    {
        if (!_acquired)
        {
            _acquired = true;
            Surface.Acquire(position, halfExtent, order.Facing, chunks);
        }

        Vector3? goal = null;
        var speed = order.Speed;
        if (order.Dying)
        {
            if (Surface.Attached && !Surface.OnFloor) Surface.Detach();
        }
        else if (order.Move != Vector3.Zero)
        {
            var aim = order.Target != null
                ? Chase(position, ref velocity, order.Target, order.MeleeReach, deltaTime, chunks)
                : order.Wander ?? position;
            var to = aim - position;
            if (to.LengthSquared() > 1e-6f) goal = Vector3.Normalize(to);
            // A stroll hands in a fraction of a unit move
            if (order.Target == null) speed *= MathF.Min(1f, order.Move.Length());
            var below = order.Target != null ? RealmMob.ChestOf(order.Target.Eye) - position : Vector3.Zero;
            if (Surface.OnCeiling && below.Y < 0f
                && below.X * below.X + below.Z * below.Z < CrawlerConfig.CeilingDropRadiusBlocks * CrawlerConfig.CeilingDropRadiusBlocks)
                Surface.Detach();
        }

        if (order.Target == null || order.Move == Vector3.Zero) _navigator.Reset();

        return Surface.Step(ref position, ref velocity, goal, speed, halfExtent, deltaTime, chunks,
            mayGrab: !order.Dying);
    }

    /// <summary>
    /// A blow that should throw the body <paramref name="direction"/> at <paramref name="speed"/>: on a wall or a
    /// ceiling it knocks the crawler loose (it lets go and falls with the shove) and this answers true; on a floor
    /// or in the air it answers false and the owner slides it like any walker.
    /// </summary>
    public bool KnockLoose(Vector3 direction, float speed, ref Vector3 velocity)
    {
        if (!Surface.Attached || Surface.OnFloor) return false;
        if (direction.LengthSquared() < 1e-6f) return true;
        Surface.Detach();
        velocity = Vector3.Normalize(direction) * speed;
        return true;
    }

    // The point the chase aims at this frame: the target's chest, or the next foothold of a path round what keeps it
    // from closing in. A stalled chase leaps when it can and plans a path when it cannot.
    private Vector3 Chase(Vector3 position, ref Vector3 velocity, IMobTarget target, float meleeReach,
        float deltaTime, ChunkManager chunks)
    {
        var chest = RealmMob.ChestOf(target.Eye);
        var aim = _navigator.Aim(position, halfExtent, chest, Surface, deltaTime, out var stalled, out var letGo);
        if (letGo) Surface.Detach();
        if (stalled && !TryLeap(position, ref velocity, target, chunks)
            && _navigator.Plan(position, halfExtent, chest, meleeReach, Surface, chunks))
            aim = _navigator.Aim(position, halfExtent, chest, Surface, 0f, out _, out _);
        return aim;
    }

    // A leap onto the target's feet: off its cooldown, from a held surface and away from it, the feet within leaping
    // distance in a clear line, on an arc no faster than a leap may launch
    private bool TryLeap(Vector3 position, ref Vector3 velocity, IMobTarget target, ChunkManager chunks)
    {
        if (_leapCooldown > 0f || !Surface.Attached) return false;
        var landing = target.Eye - Vector3.UnitY * (Camera.EyeHeight - halfExtent);
        var distance = Vector3.Distance(position, landing);
        if (distance < CrawlerConfig.LeapMinBlocks || distance > CrawlerConfig.LeapMaxBlocks) return false;

        var launch = SurfaceCrawler.LeapVelocity(position, landing, CrawlerConfig.LeapHorizontalSpeed);
        if (launch.LengthSquared() > CrawlerConfig.LeapMaxSpeed * CrawlerConfig.LeapMaxSpeed
            || Vector3.Dot(launch, Surface.Normal) <= 0f || !seesTarget(position, target, chunks))
            return false;

        Surface.Detach();
        velocity = launch;
        _leapCooldown = CrawlerConfig.LeapCooldownSeconds;
        return true;
    }
}
