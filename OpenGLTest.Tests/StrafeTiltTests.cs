using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class StrafeTiltTests
{
    public StrafeTiltTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float Walk = PlayerPhysics.BaseSpeed;

    private static Camera NewCamera()
    {
        var camera = new Camera(null, null);
        // Facing -Z, so the camera's right is +X.
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: -90f, pitch: 0f);
        return camera;
    }

    private static void Step(Camera camera, int frames, float strafe, bool moving = true)
    {
        for (var i = 0; i < frames; i++)
            camera.UpdateHeadMotion(Dt, moving ? Walk : 0f, moving, swimming: false, strafe: strafe);
    }

    // The world direction the view's up axis points (the second column of a System.Numerics look-at).
    private static Vector3 ViewUp(Camera camera)
    {
        var view = camera.GetViewMatrix();
        return new Vector3(view.M12, view.M22, view.M32);
    }

    [Fact]
    public void WalkingWithoutStrafeInputNeverTilts()
    {
        var camera = NewCamera();
        for (var i = 0; i < 240; i++)
        {
            Step(camera, 1, strafe: 0f);
            Assert.Equal(0f, camera.StrafeTiltRoll);
        }
    }

    [Fact]
    public void StrafingLeansTheViewIntoTheMoveAndItLevelsOutAfter()
    {
        var right = NewCamera();
        var left = NewCamera();
        Step(right, 120, strafe: 1f);
        Step(left, 120, strafe: -1f);

        // Leans into the move: the view's up axis tips toward the strafe side.
        Assert.True(right.StrafeTiltRoll > 0f);
        Assert.True(left.StrafeTiltRoll < 0f);
        Assert.True(ViewUp(right).X > 0f);
        Assert.True(ViewUp(left).X < 0f);

        // Stopping the strafe eases it back to level without overshooting past it.
        var previous = right.StrafeTiltRoll;
        for (var i = 0; i < 240; i++)
        {
            Step(right, 1, strafe: 0f, moving: false);
            Assert.InRange(right.StrafeTiltRoll, 0f, previous);
            previous = right.StrafeTiltRoll;
        }
        Assert.InRange(right.StrafeTiltRoll, 0f, 1e-4f);
    }

    [Fact]
    public void TheTiltNeverPassesItsBoundWhateverTheInput()
    {
        var camera = NewCamera();
        var widest = 0f;
        // An out-of-range input, held long and flipped back and forth, still stays inside the bound.
        for (var i = 0; i < 600; i++)
        {
            Step(camera, 1, strafe: (i / 90) % 2 == 0 ? 5f : -5f);
            Assert.True(MathF.Abs(camera.StrafeTiltRoll) <= StrafeTilt.MaxRadians + 1e-6f);
            widest = MathF.Max(widest, MathF.Abs(camera.StrafeTiltRoll));
        }

        // Anti-vacuity: the bound is actually reached, so it is the constant that limits the tilt.
        Assert.True(widest > StrafeTilt.MaxRadians * 0.95f);
    }

    [Fact]
    public void FlightAndDeathLevelTheTiltOut()
    {
        Assert.Equal(0f, StrafeTilt.TargetRadians(1f, flying: true, dead: false));
        Assert.Equal(0f, StrafeTilt.TargetRadians(-1f, flying: false, dead: true));

        var camera = NewCamera();
        Step(camera, 120, strafe: 1f);
        Assert.True(camera.StrafeTiltRoll > 0f);

        // Dying while still strafing: the tilt eases out instead of holding or growing.
        camera.DeathProgress = 0.5f;
        var previous = camera.StrafeTiltRoll;
        for (var i = 0; i < 240; i++)
        {
            Step(camera, 1, strafe: 1f);
            Assert.True(camera.StrafeTiltRoll <= previous);
            previous = camera.StrafeTiltRoll;
        }
        Assert.InRange(camera.StrafeTiltRoll, 0f, 1e-4f);
    }

    [Fact]
    public void TheTiltAddsToTheHitKickRollInsteadOfReplacingIt()
    {
        var kicked = NewCamera();
        var kickedStrafing = NewCamera();
        kicked.AddHitKick(new Vector3(1f, 0f, 0f));
        kickedStrafing.AddHitKick(new Vector3(1f, 0f, 0f));
        for (var i = 0; i < 6; i++)
        {
            Step(kicked, 1, strafe: 0f, moving: false);
            Step(kickedStrafing, 1, strafe: 1f, moving: false);
        }

        // Both carry the same kick; the strafing one leans further by exactly its own tilt.
        Assert.True(kicked.KickRoll > 0f);
        Assert.Equal(kicked.KickRoll, kickedStrafing.KickRoll, 5);
        Assert.True(kickedStrafing.StrafeTiltRoll > 0f);
        var kickedUp = ViewUp(kicked);
        var strafingUp = ViewUp(kickedStrafing);
        var rollKicked = MathF.Atan2(kickedUp.X, kickedUp.Y);
        var rollStrafing = MathF.Atan2(strafingUp.X, strafingUp.Y);
        Assert.Equal(rollKicked + kickedStrafing.StrafeTiltRoll, rollStrafing, 4);
    }
}
