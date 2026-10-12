using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Size-scaled hops: a larger slime launches faster on both axes, so its hop rises higher and lands
/// farther, and it crouches longer before leaving the ground.
/// </summary>
public sealed class SlimeHopTests
{
    public SlimeHopTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Fact]
    public void ALargerSlimeLaunchesFasterUpAndSideways()
    {
        for (var size = SlimeSize.Min; size < SlimeSize.Max; size++)
        {
            var smaller = Slime.HopVelocity(Vector3.UnitX, inWater: false, size);
            var larger = Slime.HopVelocity(Vector3.UnitX, inWater: false, size + 1);
            Assert.True(larger.Y > smaller.Y, $"size {size + 1} lifts {larger.Y}, size {size} {smaller.Y}");
            Assert.True(larger.X > smaller.X, $"size {size + 1} reaches {larger.X}, size {size} {smaller.X}");
            Assert.True(SlimeSize.WindupScale(size + 1) > SlimeSize.WindupScale(size));
        }

        // The smallest slime keeps the plain hop
        Assert.Equal(Slime.HopVelocity(Vector3.UnitX, inWater: false),
            Slime.HopVelocity(Vector3.UnitX, inWater: false, SlimeSize.Min));
    }

    /// <summary>One hop toward a target far along +X on a stone floor: how long the slime crouched,
    /// how high its feet rose and how far it landed from where it left the ground.</summary>
    private static (int crouchFrames, float apex, float reach) FirstHop(int size)
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var half = Slime.HalfExtentFor(size);
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(1.5f, 1.05f + half, 8.5f), size);
        var target = new Vector3(15.5f, 1.62f, 8.5f);

        var crouch = 0;
        var launchX = float.NaN;
        var apex = 0f;
        var wasAirborne = false;
        for (var i = 0; i < 1200; i++)
        {
            var windingUp = slime.IsWindingUp;
            slime.Update(Dt, chunks, target);
            if (windingUp) crouch++;
            if (windingUp && !slime.IsWindingUp)
            {
                launchX = slime.Position.X;
                wasAirborne = true;
            }
            if (!wasAirborne) continue;
            apex = MathF.Max(apex, slime.Position.Y - half - 1f);
            if (slime.IsGrounded && apex > 0.1f)
                return (crouch, apex, slime.Position.X - launchX);
        }

        Assert.Fail($"a size {size} slime never finished a hop");
        return default;
    }

    [Fact]
    public void ALargerSlimeCrouchesLongerAndHopsHigherAndFarther()
    {
        var smaller = FirstHop(SlimeSize.Min);
        for (var size = SlimeSize.Min + 1; size <= SlimeSize.Max; size++)
        {
            var larger = FirstHop(size);
            Assert.True(larger.crouchFrames > smaller.crouchFrames,
                $"size {size} crouched {larger.crouchFrames} frames, the smaller one {smaller.crouchFrames}");
            Assert.True(larger.apex > smaller.apex, $"size {size} rose {larger.apex}, the smaller one {smaller.apex}");
            Assert.True(larger.reach > smaller.reach, $"size {size} landed {larger.reach} out, the smaller one {smaller.reach}");
            smaller = larger;
        }
    }
}
