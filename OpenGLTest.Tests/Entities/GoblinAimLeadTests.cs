using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using Xunit;

namespace VoxelEngine.Tests.Entities;

public class GoblinAimLeadTests
{
    [Fact]
    public void Lead_shot_lands_on_a_strafing_target()
    {
        var from = new Vector3(0f, 1f, 0f);
        var chest = new Vector3(10f, 1f, 0f);
        var strafe = new Vector3(0f, 0f, 4f);
        const float windup = 0.3f;

        Assert.True(GoblinAim.TryLeadVelocity(from, chest, strafe, 20f, ThrownProjectile.Gravity, windup, out var v));

        // Fly the stone analytically until it is level with the chest, then compare with where the target is.
        float g = ThrownProjectile.Gravity;
        float t = (v.Y + MathF.Sqrt(v.Y * v.Y + 2f * g * (from.Y - chest.Y))) / g;
        var landing = new Vector3(from.X + v.X * t, chest.Y, from.Z + v.Z * t);
        var targetThen = chest + strafe * (windup + t);
        Assert.True(Vector3.Distance(landing, targetThen) < 0.2f);
    }

    [Fact]
    public void Lead_shot_meets_a_falling_target_lower_than_a_standing_one()
    {
        var from = new Vector3(0f, 1f, 0f);
        var chest = new Vector3(10f, 1f, 0f);

        GoblinAim.TryLeadVelocity(from, chest, Vector3.Zero, 20f, ThrownProjectile.Gravity, 0.3f, out var still);
        GoblinAim.TryLeadVelocity(from, chest, new Vector3(0f, -8f, 0f), 20f, ThrownProjectile.Gravity, 0.3f, out var falling);

        Assert.True(falling.Y < still.Y);
    }
}
