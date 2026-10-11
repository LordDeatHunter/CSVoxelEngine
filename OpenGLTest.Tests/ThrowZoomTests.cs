using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ThrowZoomTests
{
    public ThrowZoomTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float Run = PlayerPhysics.BaseSpeed * PlayerPhysics.SprintMultiplier;

    private static Camera NewCamera()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: -90f, pitch: 0f);
        return camera;
    }

    private static void Step(Camera camera, int frames, bool sprinting = false)
    {
        for (var i = 0; i < frames; i++)
            camera.UpdateHeadMotion(Dt, sprinting ? Run : 0f, moving: sprinting, swimming: false,
                sprintHeld: sprinting);
    }

    [Fact]
    public void OnlyAChargingThrowNarrowsAndAFullerChargeNeverWidens()
    {
        Assert.Equal(0f, ThrowZoom.TargetDegrees(null));
        Assert.Equal(0f, ThrowZoom.TargetDegrees(float.NaN));
        Assert.Equal(0f, ThrowZoom.TargetDegrees(0f));

        var previous = 0f;
        for (var charge = 0f; charge <= 1.0001f; charge += 0.05f)
        {
            var target = ThrowZoom.TargetDegrees(charge);
            Assert.True(target <= previous + 1e-6f);
            previous = target;
        }

        var full = ThrowZoom.TargetDegrees(1f);
        Assert.True(full < 0f);
        // Out-of-range charges clamp to the ends instead of overshooting them
        Assert.Equal(full, ThrowZoom.TargetDegrees(5f));
        Assert.Equal(0f, ThrowZoom.TargetDegrees(-3f));
    }

    [Fact]
    public void WindingUpNarrowsTheViewAndTheReleaseSpringsItBackToRest()
    {
        var camera = NewCamera();
        Step(camera, 60);
        var rest = camera.CurrentFov;
        var full = ThrowZoom.TargetDegrees(1f);

        // Charge up over the throw's wind-up, then hold it: the view narrows toward the full zoom and
        // never past the clamp a long frame could fling it to.
        var frames = (int)(WorldGenerationConfig.ThrowChargeSeconds / Dt);
        for (var i = 0; i <= frames; i++)
        {
            camera.ThrowCharge = i / (float)frames;
            Step(camera, 1);
            Assert.True(camera.CurrentFov >= rest + full - 1e-4f);
        }
        Step(camera, 180);
        Assert.Equal(rest + full, camera.CurrentFov, 1);

        // The release lets go: the view comes back, overshooting at most half the zoom the wide way,
        // and settles at the rest FOV.
        camera.ThrowCharge = null;
        for (var i = 0; i < 240; i++)
        {
            Step(camera, 1);
            Assert.True(camera.CurrentFov <= rest - full * 0.5f + 1e-4f);
        }
        Assert.Equal(rest, camera.CurrentFov, 2);
    }

    [Fact]
    public void TheZoomAddsToTheSprintWideningInsteadOfReplacingIt()
    {
        var resting = NewCamera();
        var sprinting = NewCamera();
        Step(resting, 300);
        Step(sprinting, 300, sprinting: true);
        var rest = resting.CurrentFov;
        var wide = sprinting.CurrentFov;
        Assert.True(wide > rest);

        resting.ThrowCharge = 1f;
        sprinting.ThrowCharge = 1f;
        Step(resting, 300);
        Step(sprinting, 300, sprinting: true);

        // The same charge narrows both views by the same amount, measured from their own FOV.
        Assert.True(rest - resting.CurrentFov > 0f);
        Assert.Equal(rest - resting.CurrentFov, wide - sprinting.CurrentFov, 3);
    }
}
