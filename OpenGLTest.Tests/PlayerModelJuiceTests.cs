using System;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class PlayerModelJuiceTests
{
    private const float Dt = 1f / 60f;

    private static PlayerModel Settled(Action<PlayerModel>? configure = null)
    {
        var model = new PlayerModel();
        configure?.Invoke(model);
        for (var i = 0; i < 120; i++) model.Update(Dt, 0f);
        return model;
    }

    private static float PeakDipAfterFall(float impactSpeed)
    {
        var model = Settled();
        // Falling, then supported on the next frame.
        model.Update(Dt, 0f, grounded: false, verticalVelocity: -impactSpeed);
        model.Update(Dt, 0f, grounded: false, verticalVelocity: -impactSpeed);
        var peak = 0f;
        for (var i = 0; i < 60; i++)
        {
            model.Update(Dt, 0f, grounded: true);
            peak = MathF.Max(peak, model.LandingDip);
        }
        return peak;
    }

    [Fact]
    public void ALandingDipsTheBodyInProportionToTheFallAndThenRecovers()
    {
        var soft = PeakDipAfterFall(6f);
        var hard = PeakDipAfterFall(16f);

        Assert.True(soft > 0.02f);
        Assert.True(hard > soft * 1.5f);

        var model = Settled();
        model.Update(Dt, 0f, grounded: false, verticalVelocity: -14f);
        model.Update(Dt, 0f, grounded: true);
        for (var i = 0; i < 300; i++) model.Update(Dt, 0f);
        Assert.InRange(model.LandingDip, -0.01f, 0.01f);
    }

    [Fact]
    public void ALandingBendsTheKneesAndLowersTheBodyThenReturnsToRest()
    {
        var rest = Settled().GetPose();
        var model = Settled();
        model.Update(Dt, 0f, grounded: false, verticalVelocity: -15f);
        model.Update(Dt, 0f, grounded: true);
        var lowestKnee = float.MaxValue;
        var lowestBob = float.MaxValue;
        for (var i = 0; i < 30; i++)
        {
            model.Update(Dt, 0f);
            var pose = model.GetPose();
            lowestKnee = MathF.Min(lowestKnee, pose.LeftKnee);
            lowestBob = MathF.Min(lowestBob, pose.Bob);
        }

        Assert.True(lowestKnee < rest.LeftKnee - 0.1f);
        Assert.True(lowestBob < rest.Bob - 0.01f);

        for (var i = 0; i < 300; i++) model.Update(Dt, 0f);
        Assert.Equal(rest.LeftKnee, model.GetPose().LeftKnee, 2);
    }

    [Fact]
    public void CrouchingBendsTheLegsLowersTheBodyAndStandingUpRestoresIt()
    {
        var rest = Settled().GetPose();
        var model = Settled();
        for (var i = 0; i < 60; i++) model.Update(Dt, 0f, sneaking: true);

        var crouched = model.GetPose();
        Assert.True(model.CrouchBlend > 0.9f);
        Assert.True(crouched.LeftKnee < rest.LeftKnee - 0.5f);
        Assert.True(crouched.Bob < rest.Bob - 0.1f);

        for (var i = 0; i < 90; i++) model.Update(Dt, 0f, sneaking: false);
        Assert.Equal(rest.LeftKnee, model.GetPose().LeftKnee, 2);
    }

    [Fact]
    public void SwimmingStrokesTheArmsOppositeEachOtherAndEasesBackOutOfTheWater()
    {
        var model = Settled();
        var spread = 0f;
        for (var i = 0; i < 180; i++)
        {
            model.Update(Dt, 3f, swimming: true);
            var pose = model.GetPose();
            spread = MathF.Max(spread, MathF.Abs(pose.LeftShoulder - pose.RightShoulder));
        }

        Assert.True(model.SwimBlend > 0.9f);
        Assert.True(spread > 1.5f);

        for (var i = 0; i < 240; i++) model.Update(Dt, 0f);
        Assert.True(model.SwimBlend < 0.01f);
        Assert.InRange(MathF.Abs(Settled().GetPose().LeftShoulder - model.GetPose().LeftShoulder), 0f, 0.06f);
    }

    [Fact]
    public void AFlinchThrowsTheTorsoAwayFromTheBlowAndSettles()
    {
        var front = Settled();
        front.Hurt(1f);
        front.Update(Dt, 0f);
        var back = Settled();
        back.Hurt(-1f);
        back.Update(Dt, 0f);

        Assert.True(front.Flinch > 0f);
        Assert.True(back.Flinch < 0f);

        for (var i = 0; i < 300; i++) front.Update(Dt, 0f);
        Assert.InRange(front.Flinch, -0.01f, 0.01f);
    }

    [Fact]
    public void SwappingItemsDipsTheHandAndSettlesBackToTheCarry()
    {
        var model = Settled(m => m.HoldingItem = true);
        var carry = model.GetPose().RightShoulder;

        model.NotifyEquip();
        model.Update(Dt, 0f);
        Assert.True(model.GetPose().RightShoulder < carry - 0.5f);

        for (var i = 0; i < 300; i++) model.Update(Dt, 0f);
        Assert.Equal(carry, model.GetPose().RightShoulder, 2);
    }

    [Fact]
    public void TurningTheViewLagsTheArmsBehindItAndTheyCatchUp()
    {
        var model = Settled();
        model.SetLook(-90f, 0f);
        model.SetLook(-60f, 0f);
        var lagged = model.GetPartMatrix("UpperArmR", new System.Numerics.Vector3(0f, 1.62f, 0f), -60f, 0f);
        var steady = Settled().GetPartMatrix("UpperArmR", new System.Numerics.Vector3(0f, 1.62f, 0f), -60f, 0f);

        Assert.NotEqual(steady.M31, lagged.M31);

        for (var i = 0; i < 300; i++) model.Update(Dt, 0f);
        var settled = model.GetPartMatrix("UpperArmR", new System.Numerics.Vector3(0f, 1.62f, 0f), -60f, 0f);
        Assert.Equal(steady.M31, settled.M31, 2);
    }

    [Fact]
    public void ABodyThatLagsTheLookOnlyTurnsPastItsDeadZone()
    {
        var model = new PlayerModel { BodyYawLags = true };
        model.SetLook(-90f, 0f);
        model.Update(Dt, 0f);

        model.SetLook(-70f, 0f);
        model.Update(Dt, 0f);
        Assert.InRange(model.BodyYaw, -90.5f, -88f);

        // Far past the dead zone the body is dragged along and ends up within it of the look.
        model.SetLook(20f, 0f);
        model.Update(Dt, 0f);
        Assert.True(MathF.Abs(model.BodyYaw - 20f) <= 41f);

        // Held still and moving, the body catches up to the look.
        for (var i = 0; i < 300; i++) model.Update(Dt, 3f);
        Assert.Equal(20f, model.BodyYaw, 0);
    }

    [Fact]
    public void ADeathLaysTheBodyOverAboutTheFeet()
    {
        var model = Settled();
        var eye = new System.Numerics.Vector3(0f, 1.62f, 0f);
        var upright = model.GetPartMatrix("Head", eye, -90f, 0f);

        model.DeathFall = 1f;
        var fallen = model.GetPartMatrix("Head", eye, -90f, 0f);

        // The head ends up near the ground and out behind where it stood.
        Assert.True(fallen.M42 < upright.M42 - 1f);
    }

    [Fact]
    public void AHeavySwingTakesLongerAndLeansHarderThanALightOne()
    {
        var light = new PlayerModel();
        var heavy = new PlayerModel();
        light.Punch(0f);
        heavy.Punch(1f);

        var frames = (int)(PlayerModel.MineDuration * 0.9f / Dt);
        for (var i = 0; i < frames; i++)
        {
            light.Update(Dt, 0f);
            heavy.Update(Dt, 0f);
        }

        Assert.True(heavy.MineT < light.MineT);

        var lightLean = float.MaxValue;
        var heavyLean = float.MaxValue;
        light.Punch(0f);
        heavy.Punch(1f);
        for (var i = 0; i < 40; i++)
        {
            light.Update(Dt, 0f);
            heavy.Update(Dt, 0f);
            lightLean = MathF.Min(lightLean, light.GetPose().TorsoPitch);
            heavyLean = MathF.Min(heavyLean, heavy.GetPose().TorsoPitch);
        }

        Assert.True(heavyLean < lightLean);
    }

    // The right hand's world position for a body standing at the origin facing -Z.
    private static System.Numerics.Vector3 RightHand(PlayerModel model)
    {
        var eye = new System.Numerics.Vector3(0f, 1.62f, 0f);
        return System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0f, model.HandDepth(), 0f),
            model.GetPartMatrix("ForearmR", eye, -90f, 0f));
    }

    [Fact]
    public void JumpingThrowsTheArmsOutToTheSidesAndTucksTheLegsMoreThanBefore()
    {
        var model = Settled();
        var rest = RightHand(model);
        var restKnee = model.GetPose().LeftKnee;

        for (var i = 0; i < 60; i++) model.Update(Dt, 0f, grounded: false, verticalVelocity: 6f);

        var air = RightHand(model);
        // Out to the side of the body (+X is the right hand's side) and not out in front of it.
        Assert.True(air.X > rest.X + 0.25f);
        Assert.True(air.Z > rest.Z - 0.05f);
        // Both knees are folded well under the thighs.
        var pose = model.GetPose();
        Assert.True(pose.LeftKnee < restKnee - 1f);
        Assert.True(pose.RightKnee < restKnee - 0.9f);
    }
}
