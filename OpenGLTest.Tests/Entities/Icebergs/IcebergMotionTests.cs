using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using Xunit;

namespace VoxelEngine.Tests.Entities.Icebergs;

public sealed class IcebergMotionTests
{
    private const float Dt = 1f / 60f;

    public IcebergMotionTests() => TestContent.EnsureLoaded();

    [Fact]
    public void WithNoPushTheVelocitySettlesOnTheDrift()
    {
        var drift = IcebergMotion.Drift(17, 100.0);
        var velocity = new Vector2(1f, -0.8f);
        for (var i = 0; i < 60 * 60; i++)
            velocity = IcebergMotion.Step(velocity, drift, Vector2.Zero, Dt);

        Assert.True(Vector2.Distance(velocity, drift) < 1e-4f);
    }

    [Fact]
    public void TheSamePushMovesAHeavierBergLess()
    {
        var shapes = IcebergShape.ForType(EntityRegistry.Require("iceberg")).OrderBy(s => s.Volume).ToArray();
        var lighter = shapes[0].Mass(IcebergConfig.ScaleMin);
        var heavier = shapes[^1].Mass(IcebergConfig.ScaleMax);
        Assert.True(heavier > lighter);

        var drift = IcebergMotion.Drift(5, 0.0);
        var baseline = Travel(drift, Vector2.Zero);
        var lightOffset = Travel(drift, IcebergMotion.PushAcceleration(Vector2.UnitX, lighter)) - baseline;
        var heavyOffset = Travel(drift, IcebergMotion.PushAcceleration(Vector2.UnitX, heavier)) - baseline;

        Assert.True(heavyOffset.X > 0f);
        Assert.True(lightOffset.X > heavyOffset.X);
    }

    [Fact]
    public void TheSpeedNeverExceedsTheCap()
    {
        var drift = IcebergMotion.Drift(3, 0.0);
        var push = new Vector2(IcebergConfig.MaxSpeed * 100f, -IcebergConfig.MaxSpeed * 40f);
        var velocity = new Vector2(IcebergConfig.MaxSpeed * 3f, 0f);
        for (var i = 0; i < 600; i++)
        {
            velocity = IcebergMotion.Step(velocity, drift, push, Dt);
            Assert.True(velocity.Length() <= IcebergConfig.MaxSpeed + 1e-4f);
        }
    }

    [Fact]
    public void DriftIsDeterministicPerSeedAndBoundedByTheDriftSpeed()
    {
        var distinct = new System.Collections.Generic.HashSet<Vector2>();
        for (var seed = -50; seed < 50; seed++)
        {
            foreach (var time in new[] { 0.0, 333.3, 98765.4 })
            {
                var drift = IcebergMotion.Drift(seed, time);
                Assert.Equal(drift, IcebergMotion.Drift(seed, time));
                Assert.True(drift.Length() <= IcebergConfig.DriftSpeed + 1e-5f);

                var wind = Wind.Direction(time);
                var cos = Vector2.Dot(Vector2.Normalize(drift), wind);
                Assert.True(MathF.Acos(Math.Clamp(cos, -1f, 1f)) <= IcebergConfig.DriftSpreadRadians + 1e-3f);
                if (time == 0.0)
                    distinct.Add(drift);
            }
        }

        Assert.True(distinct.Count > 1);
    }

    private static Vector2 Travel(Vector2 drift, Vector2 pushAcceleration)
    {
        var velocity = drift;
        var position = Vector2.Zero;
        for (var i = 0; i < 5 * 60; i++)
        {
            velocity = IcebergMotion.Step(velocity, drift, pushAcceleration, Dt);
            position += velocity * Dt;
        }

        return position;
    }
}
