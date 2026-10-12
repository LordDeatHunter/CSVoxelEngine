using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class SprintFovTests
{
    public SprintFovTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float Walk = PlayerPhysics.BaseSpeed;
    private const float Run = PlayerPhysics.BaseSpeed * PlayerPhysics.SprintMultiplier;
    // The flight sprint's top speed (PlayerPhysics.FlyBoostMultiplier is 10x the walk)
    private const float FlightSprint = PlayerPhysics.BaseSpeed * 10f;

    private static Camera NewCamera()
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 1.62f, 0.5f), yaw: -90f, pitch: 0f);
        return camera;
    }

    private static void Step(Camera camera, int frames, float speed, bool sprintHeld, bool moving = true)
    {
        for (var i = 0; i < frames; i++)
            camera.UpdateHeadMotion(Dt, speed, moving, swimming: false, sprintHeld: sprintHeld);
    }

    [Fact]
    public void SprintingEasesTheViewWiderAndTheSprintEndingEasesItBack()
    {
        var camera = NewCamera();
        Step(camera, 60, Walk, sprintHeld: false);
        var rest = camera.CurrentFov;
        var wide = rest + SprintFov.TargetDegrees(true, true, false, false, Run);
        Assert.True(wide > rest);

        // Eases out: never narrower than the frame before, never past the target, and gets there.
        var previous = rest;
        for (var i = 0; i < 240; i++)
        {
            Step(camera, 1, Run, sprintHeld: true);
            Assert.True(camera.CurrentFov >= previous - 1e-5f);
            Assert.True(camera.CurrentFov <= wide + 1e-4f);
            previous = camera.CurrentFov;
        }
        Assert.Equal(wide, camera.CurrentFov, 2);

        // Releasing the sprint key eases it back to the base FOV, even while still moving at speed.
        for (var i = 0; i < 240; i++)
        {
            Step(camera, 1, Run, sprintHeld: false);
            Assert.True(camera.CurrentFov <= previous + 1e-5f);
            previous = camera.CurrentFov;
        }
        Assert.Equal(rest, camera.CurrentFov, 2);
    }

    [Fact]
    public void TheFlightSprintNeverWidensPastTheOnFootSprint()
    {
        Assert.Equal(SprintFov.TargetDegrees(true, true, false, false, Run),
            SprintFov.TargetDegrees(true, true, false, false, FlightSprint));

        var onFoot = NewCamera();
        var flying = NewCamera();
        var rest = onFoot.CurrentFov;
        var widest = 0f;
        for (var i = 0; i < 600; i++)
        {
            Step(onFoot, 1, Run, sprintHeld: true);
            Step(flying, 1, FlightSprint, sprintHeld: true);
            widest = MathF.Max(widest, flying.CurrentFov);
        }

        Assert.True(widest > rest);
        Assert.True(widest <= rest + WorldGenerationConfig.SprintFovExtraDegrees + 1e-4f);
        Assert.Equal(onFoot.CurrentFov, flying.CurrentFov, 3);
    }

    [Fact]
    public void OnlyARealSprintWidensTheView()
    {
        // Sneaking, swimming, standing still with Shift held and walking without it never widen.
        Assert.Equal(0f, SprintFov.TargetDegrees(true, true, sneaking: true, false, Run));
        Assert.Equal(0f, SprintFov.TargetDegrees(true, true, false, swimming: true, Run));
        Assert.Equal(0f, SprintFov.TargetDegrees(true, moving: false, false, false, 0f));
        Assert.Equal(0f, SprintFov.TargetDegrees(sprintHeld: false, true, false, false, Run));
        // Shift held while backing up moves slower than a walk, so it is not a sprint either.
        Assert.Equal(0f, SprintFov.TargetDegrees(true, true, false, false, Walk * 0.3f));

        // Between the walk and the run the widening never shrinks as the body speeds up.
        var previous = 0f;
        for (var speed = Walk; speed <= FlightSprint; speed += 0.25f)
        {
            var target = SprintFov.TargetDegrees(true, true, false, false, speed);
            Assert.True(target >= previous);
            previous = target;
        }
    }

    [Fact]
    public void ThePunchPinchAddsToTheSprintWideningInsteadOfReplacingIt()
    {
        var resting = NewCamera();
        var sprinting = NewCamera();
        Step(resting, 300, 0f, sprintHeld: false, moving: false);
        Step(sprinting, 300, Run, sprintHeld: true);
        var rest = resting.CurrentFov;
        var wide = sprinting.CurrentFov;
        Assert.True(wide > rest);

        resting.PunchFov(-1f);
        sprinting.PunchFov(-1f);
        var restPinch = 0f;
        var widePinch = 0f;
        for (var i = 0; i < 30; i++)
        {
            Step(resting, 1, 0f, sprintHeld: false, moving: false);
            Step(sprinting, 1, Run, sprintHeld: true);
            restPinch = MathF.Max(restPinch, rest - resting.CurrentFov);
            widePinch = MathF.Max(widePinch, wide - sprinting.CurrentFov);
        }

        // The same pinch narrows both views by the same amount, measured from their own FOV...
        Assert.True(restPinch > 0.2f);
        Assert.Equal(restPinch, widePinch, 3);

        // ...and the sprinting view settles back to its widened FOV, not to the base one.
        Step(sprinting, 300, Run, sprintHeld: true);
        Assert.Equal(wide, sprinting.CurrentFov, 2);
    }
}
