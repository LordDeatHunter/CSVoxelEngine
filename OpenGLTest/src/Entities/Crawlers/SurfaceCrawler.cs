using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Crawlers;

/// <summary>
/// The body physics of a mob that walks on every surface: it clings to one face of the block grid at a time
/// (<see cref="Normal"/>, a floor, a wall or a ceiling), moves along it toward a goal, climbs onto a wall it walks
/// into, wraps around an edge it walks off, and lets go only when nothing is left to hold (then it falls, floats
/// in water and grabs the first wall it is carried into). The body is a cube, so it collides the same on every
/// face; every move is a <see cref="BoxSweep"/>, so it never enters a solid cell. GL-free; the owner keeps the
/// position and velocity and hands them in by reference. Main thread only.
/// </summary>
public sealed class SurfaceCrawler
{
    private float _commit;
    private float _wrapGrace;

    /// <summary>The outward normal of the face the crawler clings to (an axis direction); the last one held while
    /// it is not <see cref="Attached"/>.</summary>
    public Vector3 Normal { get; private set; } = Vector3.UnitY;

    /// <summary>The unit direction the crawler travels and faces, in the plane of its face.</summary>
    public Vector3 Heading { get; private set; } = -Vector3.UnitZ;

    /// <summary>Whether the crawler holds a surface; false while it falls or floats.</summary>
    public bool Attached { get; private set; }

    /// <summary>Whether the last step found the body floating in a fluid.</summary>
    public bool InWater { get; private set; }

    /// <summary>Whether the crawler stands on a floor.</summary>
    public bool OnFloor => Attached && Normal.Y > 0.5f;

    /// <summary>Whether the crawler hangs under a ceiling.</summary>
    public bool OnCeiling => Attached && Normal.Y < -0.5f;

    /// <summary>The up of the body: the held face's normal, or the world's while it falls.</summary>
    public Vector3 Up => Attached ? Normal : Vector3.UnitY;

    /// <summary>Lets go of the surface: the crawler falls from the next step on.</summary>
    public void Detach()
    {
        Attached = false;
        _commit = 0f;
        _wrapGrace = 0f;
    }

    /// <summary>Takes the surface a server reported (a client's mirror never steps): the face with
    /// <paramref name="normal"/> and <paramref name="heading"/> on it, or nothing for a zero normal.</summary>
    public void Mirror(Vector3 normal, Vector3 heading)
    {
        if (normal == Vector3.Zero)
        {
            Detach();
            Heading = Flattened(heading, Vector3.UnitY, Heading);
            return;
        }
        Attach(normal, heading);
    }

    /// <summary>
    /// Takes hold of the nearest surface within reach of a body of <paramref name="halfExtent"/> at
    /// <paramref name="position"/> (a spawn, a save restore): the floor first, then a wall, then the ceiling,
    /// heading at <paramref name="angle"/> on it (<see cref="CrawlFrame.HeadingOf"/>). With none in reach it
    /// stays loose and falls.
    /// </summary>
    public void Acquire(Vector3 position, float halfExtent, float angle, ChunkManager chunks)
    {
        var half = new Vector3(halfExtent);
        ReadOnlySpan<int> order = [1, 3, 4, 5, 6, 2];
        foreach (var index in order)
        {
            var normal = CrawlFrame.NormalOf(index);
            var axis = AxisOf(normal);
            BoxSweep.MoveOnAxis(position, -Component(normal, axis) * CrawlerConfig.ClingReachBlocks, axis, half, chunks,
                out var found);
            if (!found) continue;
            Attach(normal, CrawlFrame.HeadingOf(normal, angle));
            return;
        }
        Detach();
    }

    /// <summary>
    /// Advances the body one frame. <paramref name="goal"/> is the unit direction it wants to travel in the world
    /// (null to stop); on a surface the crawler follows its part along the face at <paramref name="speed"/>, and a
    /// goal that points through or away from the face keeps the heading it already has. Returns the speed of a
    /// landing made this frame (0 for none). <paramref name="mayGrab"/> false (a dying body) never takes hold of
    /// a wall it falls against.
    /// </summary>
    public float Step(ref Vector3 position, ref Vector3 velocity, Vector3? goal, float speed, float halfExtent,
        float deltaTime, ChunkManager chunks, bool mayGrab = true)
    {
        var half = new Vector3(halfExtent);
        // A body under water on a floor lets go and floats up instead of walking the lake bed
        if (OnFloor && Submerged(position, halfExtent, chunks)) Detach();
        if (!Attached) return Fall(ref position, ref velocity, goal, speed, half, deltaTime, chunks, mayGrab);

        Crawl(ref position, ref velocity, goal, speed, half, deltaTime, chunks);
        return 0f;
    }

    private void Crawl(ref Vector3 position, ref Vector3 velocity, Vector3? goal, float speed, Vector3 half,
        float deltaTime, ChunkManager chunks)
    {
        InWater = false;
        _commit = MathF.Max(0f, _commit - deltaTime);
        var wrapping = _wrapGrace > 0f;
        if (goal is { } wanted && _commit <= 0f && !wrapping)
        {
            var along = wanted - Normal * Vector3.Dot(wanted, Normal);
            if (along.LengthSquared() >= CrawlerConfig.MinTangentShare * CrawlerConfig.MinTangentShare)
                Heading = TurnToward(Vector3.Normalize(along), deltaTime);
        }

        var moveSpeed = goal != null ? speed : 0f;
        // Rounding an edge it keeps moving until it has the new face under it
        if (wrapping) moveSpeed = MathF.Max(moveSpeed, CrawlerConfig.WrapMinSpeed);
        velocity = velocity.Damp(Heading * moveSpeed, CrawlerConfig.MoveAccel, deltaTime);
        velocity -= Normal * Vector3.Dot(velocity, Normal);
        if (moveSpeed <= 0f && velocity.LengthSquared() < 1e-4f) velocity = Vector3.Zero;

        var normalAxis = AxisOf(Normal);
        var climbAxis = -1;
        var climbSign = 0f;
        var climbShare = 0f;
        for (var axis = 0; axis < 3; axis++)
        {
            if (axis == normalAxis) continue;
            var delta = Component(velocity, axis) * deltaTime;
            if (delta == 0f) continue;
            position = BoxSweep.MoveOnAxis(position, delta, axis, half, chunks, out var blocked);
            if (!blocked) continue;
            velocity = WithComponent(velocity, axis, 0f);
            // A wall that carries enough of the heading is climbed; a glancing one is slid along
            var share = Component(Heading, axis) * MathF.Sign(delta);
            if (share < CrawlerConfig.ClimbShare || share <= climbShare) continue;
            climbAxis = axis;
            climbSign = MathF.Sign(delta);
            climbShare = share;
        }

        if (climbAxis >= 0 && moveSpeed > 0f)
        {
            // Up the wall, away from the face it came along
            var from = Normal;
            Attach(AxisVector(climbAxis) * -climbSign, from);
            velocity = Heading * moveSpeed;
            _commit = CrawlerConfig.CommitSeconds;
            return;
        }

        var down = -Component(Normal, normalAxis);
        BoxSweep.MoveOnAxis(position, down * CrawlerConfig.ClingReachBlocks, normalAxis, half, chunks, out var held);
        if (held)
        {
            position = BoxSweep.MoveOnAxis(position,
                down * MathF.Min(CrawlerConfig.ClingReachBlocks, CrawlerConfig.ClingSpeed * deltaTime), normalAxis, half,
                chunks, out _);
            _wrapGrace = 0f;
            return;
        }

        if (wrapping)
        {
            _wrapGrace -= deltaTime;
            if (_wrapGrace <= 0f) Detach();
            return;
        }

        if (TryWrap(position, half, chunks))
            velocity = Heading * MathF.Max(moveSpeed, CrawlerConfig.WrapMinSpeed);
        else
            Detach();
    }

    // Walked off an edge: the face it left is the side of the block it stood on. The body is tried a little
    // "down" past the edge, and the first face found behind it there (the one the heading leans on most first)
    // is taken, heading on down it.
    private bool TryWrap(Vector3 position, Vector3 half, ChunkManager chunks)
    {
        var normalAxis = AxisOf(Normal);
        var down = -Component(Normal, normalAxis);
        var lowered = BoxSweep.MoveOnAxis(position, down * (half.X + CrawlerConfig.EdgeDropBlocks), normalAxis, half,
            chunks, out _);

        var first = (normalAxis + 1) % 3;
        var second = (normalAxis + 2) % 3;
        if (MathF.Abs(Component(Heading, second)) > MathF.Abs(Component(Heading, first)))
            (first, second) = (second, first);

        return TryWrapOnto(first, lowered, half, chunks) || TryWrapOnto(second, lowered, half, chunks);
    }

    private bool TryWrapOnto(int axis, Vector3 lowered, Vector3 half, ChunkManager chunks)
    {
        var along = Component(Heading, axis);
        if (MathF.Abs(along) < 0.1f) return false;
        var sign = MathF.Sign(along);
        BoxSweep.MoveOnAxis(lowered, -sign * CrawlerConfig.ClingReachBlocks, axis, half, chunks, out var found);
        if (!found) return false;

        var from = Normal;
        Attach(AxisVector(axis) * sign, -from);
        _commit = CrawlerConfig.CommitSeconds;
        _wrapGrace = CrawlerConfig.WrapGraceSeconds;
        return true;
    }

    private float Fall(ref Vector3 position, ref Vector3 velocity, Vector3? goal, float speed, Vector3 half,
        float deltaTime, ChunkManager chunks, bool mayGrab)
    {
        var bottom = position.Y - half.Y;
        var (cellX, cellY, cellZ) = new Vector3(position.X, bottom, position.Z).BlockCell();
        var surfaceY = chunks.GetFluidSurfaceGlobal(cellX, cellY, cellZ);
        InWater = bottom < surfaceY;
        if (InWater)
        {
            velocity.Y = BodyMotion.FloatTowardSurface(velocity.Y, surfaceY, bottom, deltaTime);
            var paddle = goal is { } wanted && mayGrab ? new Vector3(wanted.X, 0f, wanted.Z) : Vector3.Zero;
            if (paddle.LengthSquared() > 1e-6f)
            {
                paddle = Vector3.Normalize(paddle) * speed * CrawlerConfig.SwimSpeedFactor;
                velocity.X = MathUtils.Damp(velocity.X, paddle.X, CrawlerConfig.MoveAccel, deltaTime);
                velocity.Z = MathUtils.Damp(velocity.Z, paddle.Z, CrawlerConfig.MoveAccel, deltaTime);
            }
            else
            {
                BodyMotion.DragInWater(ref velocity, deltaTime);
            }
        }
        else
        {
            velocity.Y = BodyMotion.Fall(velocity.Y, deltaTime);
        }

        var move = velocity * deltaTime;
        position = BoxSweep.MoveOnAxis(position, move.X, 0, half, chunks, out var hitX);
        position = BoxSweep.MoveOnAxis(position, move.Z, 2, half, chunks, out var hitZ);
        position = BoxSweep.MoveOnAxis(position, move.Y, 1, half, chunks, out var hitY);
        if (hitX) velocity.X = 0f;
        if (hitZ) velocity.Z = 0f;

        if (hitY && move.Y < 0f)
        {
            var impact = -velocity.Y;
            velocity.Y = 0f;
            Attach(Vector3.UnitY, Flattened(Heading, Vector3.UnitY, -Vector3.UnitZ));
            return impact;
        }

        if (hitY) velocity.Y = 0f;
        if (!mayGrab || !(hitX || hitZ)) return 0f;

        // Carried into a wall: it takes hold, climbing out of the water or on down toward the ground
        var wall = hitX ? new Vector3(-MathF.Sign(move.X), 0f, 0f) : new Vector3(0f, 0f, -MathF.Sign(move.Z));
        Attach(wall, InWater ? Vector3.UnitY : -Vector3.UnitY);
        velocity = Vector3.Zero;
        _commit = CrawlerConfig.CommitSeconds;
        return 0f;
    }

    private void Attach(Vector3 normal, Vector3 heading)
    {
        Normal = normal;
        Heading = Flattened(heading, normal, Heading);
        Attached = true;
    }

    // The heading turned about the normal toward a target in the face, by an eased share of the angle between
    // them, so a target straight behind is turned to as well
    private Vector3 TurnToward(Vector3 target, float deltaTime)
    {
        var side = Vector3.Cross(Normal, Heading);
        var angle = MathF.Atan2(Vector3.Dot(side, target), Vector3.Dot(Heading, target));
        angle *= 1f - MathF.Exp(-CrawlerConfig.TurnRate * deltaTime);
        return Flattened(Heading * MathF.Cos(angle) + side * MathF.Sin(angle), Normal, target);
    }

    // The unit part of a direction across a normal; a direction along the normal falls back to the given one,
    // and that to any direction in the face
    private static Vector3 Flattened(Vector3 direction, Vector3 normal, Vector3 fallback)
    {
        var flat = direction - normal * Vector3.Dot(direction, normal);
        if (flat.LengthSquared() > 1e-6f) return Vector3.Normalize(flat);
        flat = fallback - normal * Vector3.Dot(fallback, normal);
        if (flat.LengthSquared() > 1e-6f) return Vector3.Normalize(flat);
        return AxisVector((AxisOf(normal) + 1) % 3);
    }

    // Whether the top of the body is under a fluid surface
    private static bool Submerged(Vector3 position, float halfExtent, ChunkManager chunks)
    {
        var top = position.Y + halfExtent;
        var (x, y, z) = new Vector3(position.X, top, position.Z).BlockCell();
        return chunks.GetFluidSurfaceGlobal(x, y, z) > top;
    }

    private static int AxisOf(Vector3 normal) =>
        MathF.Abs(normal.X) > 0.5f ? 0 : MathF.Abs(normal.Y) > 0.5f ? 1 : 2;

    private static Vector3 AxisVector(int axis) =>
        axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

    private static float Component(Vector3 value, int axis) =>
        axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };

    private static Vector3 WithComponent(Vector3 value, int axis, float component) => axis switch
    {
        0 => value with { X = component },
        1 => value with { Y = component },
        _ => value with { Z = component },
    };
}
