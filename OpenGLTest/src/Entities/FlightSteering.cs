using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities;

/// <summary>
/// The GL-free, state-free steering rules of a flying mob (<see cref="RiggedMob.Fly"/>): where it
/// hovers over a chased target, the floor under it, the height it keeps above that floor, the velocity
/// it steers toward a goal and the wall probe that slides it along a solid (or lifts it over one). The
/// body itself still moves through <see cref="BoxSweep"/>, so it can never enter a solid cell whatever
/// the steering asks. Main thread (it reads the live chunk map).
/// </summary>
public static class FlightSteering
{
    /// <summary>The point a flyer chasing a target hovers at: <paramref name="hoverHeight"/> above the
    /// target's <paramref name="targetEye"/>.</summary>
    public static Vector3 ChaseGoal(Vector3 targetEye, float hoverHeight) =>
        targetEye + new Vector3(0f, hoverHeight, 0f);

    /// <summary>
    /// How far the floor lies under <paramref name="bottomY"/> (the body's base) in the column of
    /// <paramref name="center"/>: the gap down to the top of the first solid or fluid cell (a flyer hovers
    /// over water, never in it), 0 when the base already touches it; null when there is none within
    /// <paramref name="maxDepth"/> (open sky, or unloaded cells, which read as air).
    /// </summary>
    public static float? GroundBelow(ChunkManager chunks, Vector3 center, float bottomY, float maxDepth)
    {
        var x = (int)MathF.Floor(center.X);
        var z = (int)MathF.Floor(center.Z);
        var top = (int)MathF.Floor(bottomY);
        var bottom = (int)MathF.Floor(bottomY - maxDepth);
        for (var y = top; y >= bottom; y--)
        {
            var id = chunks.GetBlockGlobal(x, y, z);
            if (BlockType.IsSolid(id) || BlockType.IsFluid(id))
                return MathF.Max(0f, bottomY - (y + 1));
        }
        return null;
    }

    /// <summary>
    /// The body-center height a flyer steers to for a goal at <paramref name="goalY"/> over a floor whose
    /// top is <paramref name="groundTop"/> (null: no floor rule). Idle (<paramref name="overGround"/>) it
    /// stays inside its hover band above the floor; chasing it only keeps its minimum clearance, so it
    /// follows a target up a cliff or over a pit.
    /// </summary>
    public static float GoalHeight(float goalY, float? groundTop, float halfHeight, FlightTuning flight, bool overGround)
    {
        if (groundTop is not { } ground) return goalY;
        var lowest = ground + halfHeight + (overGround ? flight.HoverMinBlocks : flight.MinClearanceBlocks);
        if (goalY < lowest) return lowest;
        return overGround ? MathF.Min(goalY, ground + halfHeight + flight.HoverMaxBlocks) : goalY;
    }

    /// <summary>The gentle hover bob added to the goal height at <paramref name="time"/> seconds, offset by
    /// a mob's own <paramref name="phase"/> (radians) so a flock does not bob in step.</summary>
    public static float BobOffset(float time, float phase, FlightTuning flight) =>
        flight.BobBlocks * MathF.Sin(MathF.Tau * flight.BobHz * time + phase);

    /// <summary>
    /// The velocity a flyer at <paramref name="position"/> steers toward to reach <paramref name="goal"/>:
    /// horizontally the cruise speed toward it, easing off within <see cref="MobConfig.MobFlightArriveBlocks"/>
    /// so it settles instead of circling; vertically <see cref="MobConfig.MobFlightAltitudeGain"/> per block
    /// of height error, capped by the vertical speed. Both scaled by <paramref name="speedFactor"/> (a
    /// slowing status effect, or a stroll).
    /// </summary>
    public static Vector3 DesiredVelocity(Vector3 position, Vector3 goal, FlightTuning flight, float speedFactor)
    {
        var to = new Vector3(goal.X - position.X, 0f, goal.Z - position.Z);
        var distance = to.Length();
        var horizontal = distance > 1e-4f
            ? to / distance * (flight.CruiseSpeed * MathUtils.Clamp01(distance / MobConfig.MobFlightArriveBlocks))
            : Vector3.Zero;
        var climb = Math.Clamp((goal.Y - position.Y) * MobConfig.MobFlightAltitudeGain,
            -flight.VerticalSpeed, flight.VerticalSpeed);
        return new Vector3(horizontal.X, climb, horizontal.Z) * speedFactor;
    }

    /// <summary>
    /// The wall probe: each horizontal component of <paramref name="desired"/> that would carry the body
    /// (<paramref name="halfExtents"/> about <paramref name="position"/>) into a solid cell within
    /// <see cref="MobConfig.MobFlightProbeBlocks"/> is dropped, so the flyer slides along the wall on the
    /// other axis, and it climbs at <paramref name="climbSpeed"/> instead to get over it. A vertical
    /// component into a floor or a ceiling is dropped last, so a flyer under a roof slides along it.
    /// </summary>
    public static Vector3 AvoidSolids(Vector3 position, Vector3 halfExtents, Vector3 desired, float climbSpeed,
        ChunkManager chunks)
    {
        var blockedSide = false;
        if (Blocked(position, halfExtents, 0, desired.X, chunks))
        {
            desired.X = 0f;
            blockedSide = true;
        }
        if (Blocked(position, halfExtents, 2, desired.Z, chunks))
        {
            desired.Z = 0f;
            blockedSide = true;
        }
        if (blockedSide) desired.Y = MathF.Max(desired.Y, climbSpeed);
        if (Blocked(position, halfExtents, 1, desired.Y, chunks)) desired.Y = 0f;
        return desired;
    }

    // Whether moving along one axis in the sign of a component would meet a solid within the probe
    private static bool Blocked(Vector3 position, Vector3 halfExtents, int axis, float component, ChunkManager chunks) =>
        MathF.Abs(component) > 1e-4f
        && BoxSweep.IsColliding(BoxSweep.OffsetAxis(position, axis, MathF.Sign(component) * MobConfig.MobFlightProbeBlocks),
            halfExtents, chunks);
}
