using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CameraJuiceTests
{
    public CameraJuiceTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static Camera NewCamera()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: -90f, pitch: 0f);
        return camera;
    }

    private static void Step(Camera camera, int frames)
    {
        for (var i = 0; i < frames; i++)
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
    }

    [Fact]
    public void AHitRollsTheViewAwayFromItsSideAndSettles()
    {
        var fromLeft = NewCamera();
        var fromRight = NewCamera();
        // The default look faces -Z, so +X is the camera's right.
        fromLeft.AddHitKick(new Vector3(1f, 0f, 0f));
        fromRight.AddHitKick(new Vector3(-1f, 0f, 0f));
        Step(fromLeft, 3);
        Step(fromRight, 3);

        Assert.True(fromLeft.KickRoll > 0f);
        Assert.True(fromRight.KickRoll < fromLeft.KickRoll);

        Step(fromLeft, 300);
        Assert.InRange(fromLeft.KickRoll, -0.005f, 0.005f);
        Assert.InRange(fromLeft.LandingDip, -0.05f, 0.05f);
    }

    [Fact]
    public void ALandedPunchPinchesTheFovAndItReturns()
    {
        var camera = NewCamera();
        Step(camera, 120);
        var rest = camera.CurrentFov;

        camera.PunchFov(-1f);
        var narrowest = rest;
        for (var i = 0; i < 20; i++)
        {
            camera.UpdateHeadMotion(Dt, 0f, moving: false, swimming: false);
            narrowest = MathF.Min(narrowest, camera.CurrentFov);
        }

        Assert.True(narrowest < rest - 0.2f);

        Step(camera, 300);
        Assert.Equal(rest, camera.CurrentFov, 1);
    }

    [Fact]
    public void ADeathTipsAndLowersTheViewAndRespawnRestoresIt()
    {
        var camera = NewCamera();
        Step(camera, 2);
        var upright = camera.GetViewMatrix();

        camera.DeathProgress = 1f;
        Step(camera, 2);
        var dead = camera.GetViewMatrix();
        Assert.NotEqual(upright, dead);

        camera.DeathProgress = 0f;
        Step(camera, 2);
        Assert.Equal(upright, camera.GetViewMatrix());
    }
}
