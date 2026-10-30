using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Entities.Icebergs;

/// <summary>
/// How an iceberg moves on the water, as (x, z) velocities: it settles on a drift along the world
/// wind, turned a little per berg, and a push accelerates it in inverse proportion to its mass, so
/// the same push moves a big berg less. GL-free and state-free.
/// </summary>
public static class IcebergMotion
{
    // Salt that keeps the drift angle's hash apart from any other per-seed hash.
    private const ulong DriftSalt = 0xD1F7_A5C3_0B6E_4291UL;

    /// <summary>
    /// The velocity a berg with <paramref name="seed"/> drifts at, at lighting time
    /// <paramref name="lightingTime"/>: <see cref="Wind.Direction"/> turned by a hashed angle within
    /// <see cref="IcebergConfig.DriftSpreadRadians"/>, at <see cref="IcebergConfig.DriftSpeed"/>.
    /// </summary>
    public static Vector2 Drift(int seed, double lightingTime)
    {
        var wind = Wind.Direction(lightingTime);
        var hash = SplitMix64.Mix((ulong)(uint)seed ^ DriftSalt);
        var unit = (hash >> 40) / (float)(1UL << 24); // 0..1
        var angle = (unit * 2f - 1f) * IcebergConfig.DriftSpreadRadians;
        var (sin, cos) = MathF.SinCos(angle);
        var turned = new Vector2(wind.X * cos - wind.Y * sin, wind.X * sin + wind.Y * cos);
        return turned * IcebergConfig.DriftSpeed;
    }

    /// <summary>
    /// One step of a berg's velocity: the water damps it toward <paramref name="drift"/> at
    /// <see cref="IcebergConfig.WaterDragRate"/>, the push adds its acceleration over
    /// <paramref name="dt"/>, and the speed is capped at <see cref="IcebergConfig.MaxSpeed"/>.
    /// </summary>
    public static Vector2 Step(Vector2 velocity, Vector2 drift, Vector2 pushAcceleration, float dt)
    {
        var damped = new Vector2(
            MathUtils.Damp(velocity.X, drift.X, IcebergConfig.WaterDragRate, dt),
            MathUtils.Damp(velocity.Y, drift.Y, IcebergConfig.WaterDragRate, dt));
        var next = damped + pushAcceleration * MathF.Max(0f, dt);
        var speed = next.Length();
        return speed > IcebergConfig.MaxSpeed ? next * (IcebergConfig.MaxSpeed / speed) : next;
    }

    /// <summary>
    /// The acceleration the bodies pushing a berg of <paramref name="mass"/> give it:
    /// <see cref="IcebergConfig.PlayerPushForce"/> along the summed push directions, over the mass.
    /// </summary>
    public static Vector2 PushAcceleration(Vector2 summedPushDirections, float mass) =>
        summedPushDirections * IcebergConfig.PlayerPushForce / mass;
}
