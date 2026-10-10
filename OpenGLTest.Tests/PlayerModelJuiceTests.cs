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

    private static System.Numerics.Vector3 Origin(PlayerModel model, string part) =>
        System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,
            model.GetPartMatrix(part, new System.Numerics.Vector3(0f, 1.62f, 0f), -90f, 0f));

    private static System.Numerics.Vector3 Hand(PlayerModel model, string forearm) =>
        System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0f, model.HandDepth(), 0f),
            model.GetPartMatrix(forearm, new System.Numerics.Vector3(0f, 1.62f, 0f), -90f, 0f));

    private static PlayerModel Swimmer()
    {
        var model = new PlayerModel();
        for (var i = 0; i < 120; i++) model.Update(Dt, 0f, swimming: true);
        return model;
    }

    private static void Advance(PlayerModel model, float seconds, float forward = 0f)
    {
        for (var t = 0f; t < seconds; t += Dt) model.Update(Dt, 0f, swimming: true, swimForward: forward);
    }

    [Fact]
    public void ASwimStrokeReachesForwardThenThrustsTheArmsOutAndCreepsBackToTheFront()
    {
        var model = Swimmer();
        float Spread() => MathF.Abs(Hand(model, "ForearmL").X - Hand(model, "ForearmR").X);
        float Ahead() => Origin(model, "UpperArmR").Z - Hand(model, "ForearmR").Z;

        // Ready: both hands out in front of the shoulders, close together.
        var readySpread = Spread();
        Assert.True(Ahead() > 0.35f);

        model.TriggerSwimStroke();
        Advance(model, PlayerPhysics.SwimStrokeSeconds * 0.25f);
        // Thrust and hold: the arms are out at the sides, no longer in front.
        Assert.True(Spread() > readySpread + 0.6f);
        Assert.True(Ahead() < 0.2f);
        var heldSpread = Spread();

        // Recovery is slow: partway through the arms are still well out ...
        Advance(model, PlayerPhysics.SwimStrokeSeconds * 0.25f);
        Assert.True(Spread() > readySpread + 0.2f);
        Assert.True(Spread() < heldSpread);

        // ... and by the end of the stroke they are back in front, ready for the next.
        Advance(model, PlayerPhysics.SwimStrokeSeconds * 0.6f);
        Assert.Equal(1f, model.SwimStrokeProgress);
        Assert.InRange(Spread(), readySpread - 0.1f, readySpread + 0.1f);
        Assert.True(Ahead() > 0.35f);
    }

    [Fact]
    public void TheArmsThrustOutQuicklyAndRecoverSlowly()
    {
        var model = Swimmer();
        float Spread() => MathF.Abs(Hand(model, "ForearmL").X - Hand(model, "ForearmR").X);
        var ready = Spread();

        model.TriggerSwimStroke();
        Advance(model, PlayerPhysics.SwimStrokeSeconds * 0.22f);
        var thrust = Spread() - ready;
        Assert.True(thrust > 0.6f);

        // Moving back the same distance takes far longer than moving out did.
        var framesBack = 0;
        while (Spread() - ready > thrust * 0.5f && framesBack < 600)
        {
            model.Update(Dt, 0f, swimming: true);
            framesBack++;
        }
        Assert.True(framesBack * Dt > PlayerPhysics.SwimStrokeSeconds * 0.2f);
    }

    [Fact]
    public void TheLegsKickWithTheThrustAndDrawBackUpDuringTheRecovery()
    {
        var model = Swimmer();
        model.TriggerSwimStroke();
        Advance(model, PlayerPhysics.SwimStrokeSeconds * 0.22f);
        var kicked = model.GetPose().LeftKnee;

        Advance(model, PlayerPhysics.SwimStrokeSeconds * 0.7f);
        var drawnUp = model.GetPose().LeftKnee;

        Assert.True(drawnUp < kicked - 0.3f);
    }

    [Fact]
    public void TheWholeBodyTipsTowardTheSwimDirectionAndLevelsOutWhenStill()
    {
        var still = Swimmer();
        var heading = Swimmer();
        Advance(heading, 1f, forward: 5f);

        // Tipping forward carries the head out in front of where it floats when still, and the feet back.
        Assert.True(Origin(heading, "Head").Z < Origin(still, "Head").Z - 0.3f);
        Assert.True(Origin(heading, "ShinL").Z > Origin(still, "ShinL").Z + 0.1f);

        Advance(heading, 2f, forward: 0f);
        Assert.Equal(Origin(still, "Head").Z, Origin(heading, "Head").Z, 1);
    }

    [Fact]
    public void ARemoteSwimmerStartsItsOwnStrokesWhileItMoves()
    {
        var model = new PlayerModel { AutoSwimStroke = true };
        var restarts = 0;
        var last = 1f;
        for (var i = 0; i < 360; i++)
        {
            model.Update(Dt, 3f, swimming: true);
            if (model.SwimStrokeProgress < last - 0.5f) restarts++;
            last = model.SwimStrokeProgress;
        }

        Assert.True(restarts >= 2);

        // Standing still in the water it makes none.
        var idle = new PlayerModel { AutoSwimStroke = true };
        for (var i = 0; i < 240; i++) idle.Update(Dt, 0f, swimming: true);
        Assert.Equal(1f, idle.SwimStrokeProgress);
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

    [Fact]
    public void ThePunchChambersWithTheElbowOutToTheSideAndTheWristPointingForward()
    {
        var model = Settled();
        var eye = new System.Numerics.Vector3(0f, 1.62f, 0f);
        System.Numerics.Vector3 Origin(string part) =>
            System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, model.GetPartMatrix(part, eye, -90f, 0f));
        var restElbow = Origin("ForearmR");

        model.Punch();
        model.Update(PlayerModel.MineDuration * 0.22f, 0f);
        var shoulder = Origin("UpperArmR");
        var elbow = Origin("ForearmR");
        var hand = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0f, model.HandDepth(), 0f),
            model.GetPartMatrix("ForearmR", eye, -90f, 0f));

        // Shoulder-to-elbow reaches out to the side ...
        Assert.True(elbow.X > restElbow.X + 0.12f);
        Assert.True(MathF.Abs(elbow.X - shoulder.X) > MathF.Abs(elbow.Z - shoulder.Z) * 0.8f);
        // ... while elbow-to-wrist points mostly to the front.
        Assert.True(-(hand.Z - elbow.Z) > MathF.Abs(hand.X - elbow.X));

        // By the end of the thrust the elbow has come back in and the fist is out in front.
        model.Update(PlayerModel.MineDuration * 0.33f, 0f);
        var thrustElbow = Origin("ForearmR");
        Assert.True(thrustElbow.X < elbow.X - 0.05f);
    }

    // ---- the slice ----

    private static PlayerModel Slicer(int direction, out System.Numerics.Vector3 eye)
    {
        var model = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++) model.Update(Dt, 0f);
        eye = new System.Numerics.Vector3(0.5f, 70f, 0.5f);
        model.StartSlice(direction);
        return model;
    }

    // Everything is sampled at a slice progress; the slice lasts SliceDuration.
    private static void To(PlayerModel model, float progress) =>
        model.Update(PlayerModel.SliceDuration * progress - model.SliceProgress * PlayerModel.SliceDuration, 0f);

    [Fact]
    public void TheStrikeIsMuchFasterThanTheWindupAndTheRecovery()
    {
        var model = Slicer(1, out var eye);
        System.Numerics.Vector3 Hand() => System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,
            model.GetHandAnchorMatrix(eye, -90f));

        To(model, 0.26f);
        var windupEnd = Hand().X;
        To(model, 0.49f);
        var strikeEnd = Hand().X;
        To(model, 1f);
        var rest = Hand().X;

        // Speed in blocks of sideways travel per unit of progress.
        var strikeSpeed = MathF.Abs(strikeEnd - windupEnd) / 0.23f;
        var recoverySpeed = MathF.Abs(rest - strikeEnd) / 0.51f;
        Assert.True(strikeSpeed > recoverySpeed * 2f);
        Assert.True(strikeSpeed > 0.5f);
    }

    [Fact]
    public void EverySliceCutsFromTheTopTowardTheBottomWhicheverWayItSwings()
    {
        foreach (var direction in new[] { 1, -1 })
        {
            var model = Slicer(direction, out var eye);
            float HandY() => System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,
                model.GetHandAnchorMatrix(eye, -90f)).Y;

            To(model, 0.26f);
            var start = HandY();
            To(model, PlayerModel.SliceImpactProgress);
            var middle = HandY();
            To(model, 0.49f);
            var end = HandY();

            Assert.True(start > middle && middle > end, $"direction {direction}: {start} {middle} {end}");
            // A small diagonal, not a chop: a couple of tenths of a block.
            Assert.InRange(start - end, 0.15f, 0.5f);
        }
    }

    [Fact]
    public void TheWholeBodyTwistsIntoTheSliceAndMirrorsForTheReturn()
    {
        // The yaw of the body's right axis in the horizontal plane, positive toward the front.
        float BodyTurn(PlayerModel model, System.Numerics.Vector3 eye, string part)
        {
            var axis = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX,
                model.GetPartMatrix(part, eye, -90f, 0f));
            return MathF.Atan2(-axis.Z, axis.X);
        }

        foreach (var direction in new[] { 1, -1 })
        {
            var model = Slicer(direction, out var eye);
            To(model, 0.26f);
            var coiled = BodyTurn(model, eye, "Torso");
            To(model, 0.49f);
            var struck = BodyTurn(model, eye, "Torso");

            // The shoulders swing through a large angle, in the direction of the cut ...
            Assert.True((struck - coiled) * direction > 0.6f, $"direction {direction}: {coiled} -> {struck}");
            // ... and it is not only the shoulders: the arm's root (the whole body) turns too.
            Assert.True(MathF.Abs(BodyTurn(model, eye, "UpperArmR") - BodyTurn(model, eye, "ThighL")) > 0.05f
                        || MathF.Abs(BodyTurn(model, eye, "UpperArmR")) > 0.15f);
        }
    }

    [Fact]
    public void TheSliceSinksTheBodyIntoTheStrikeAndTheFeetStayPlanted()
    {
        var model = Slicer(1, out var eye);
        System.Numerics.Vector3 Foot() => System.Numerics.Vector3.Transform(
            new System.Numerics.Vector3(0f, -0.7f, 0f), model.GetPartMatrix("ShinL", eye, -90f, 0f));
        var restFoot = Foot();
        var restKnee = model.GetPose().LeftKnee;

        To(model, PlayerModel.SliceImpactProgress);
        // The knees bend into the blow ...
        Assert.True(model.GetPose().LeftKnee < restKnee - 0.2f);
        // ... while the feet do not slide round the body.
        Assert.True(System.Numerics.Vector3.Distance(Foot(), restFoot) < 0.25f);
    }

    // ---- attachment ----

    [Fact]
    public void TheShouldersAndTheNeckStayOnTheTorsoThroughEveryAnimation()
    {
        var eye = new System.Numerics.Vector3(0f, 1.62f, 0f);
        foreach (var mode in PlayerPreviewState.AllModes)
        foreach (var lookPitch in new[] { 0f, 35f })
        {
            var model = new PlayerModel();
            var state = new PlayerPreviewState(model);
            Assert.True(state.SetMode(mode));
            var hip = model.Rig.Parts["Torso"].Pivot;

            for (var frame = 0; frame < 420; frame++)
            {
                state.Update(Dt);
                if (frame % 3 != 0) continue;

                System.Numerics.Matrix4x4.Invert(model.GetPartMatrix("Torso", eye, -90f, lookPitch), out var toTorso);
                // A joint's place in the torso's own frame, against where it rests on the torso.
                System.Numerics.Vector3 Local(string part) =>
                    System.Numerics.Vector3.Transform(
                        model.GetPartMatrix(part, eye, -90f, lookPitch).Translation, toTorso);

                // The arms may also slide in toward the torso as they lift (the shoulder socket, 0.09), so the bound is the pull
                // plus the socket; the neck has neither.
                foreach (var (part, bound) in new[] { ("UpperArmL", 0.18f), ("UpperArmR", 0.18f), ("Head", 0.005f) })
                {
                    var rest = model.Rig.Parts[part].Pivot - hip;
                    var offset = System.Numerics.Vector3.Distance(Local(part), rest);
                    // The arms may be pulled a little toward where they reach (the rubber band), never off the body.
                    Assert.True(offset <= bound,
                        $"{mode} frame {frame} (look pitch {lookPitch}): {part} is {offset:F3} blocks off its place on the torso.");
                }
            }
        }
    }

    [Fact]
    public void ARetractedArmPullsItsShoulderOutAndTheShoulderSettlesBack()
    {
        var model = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++) model.Update(Dt, 0f);
        Assert.Equal(0f, model.ShoulderPull(true).Length(), 3);

        model.StartThrowCharge();
        model.UpdateThrowCharge(1f);
        for (var i = 0; i < 60; i++) model.Update(Dt, 0f);
        var cocked = model.ShoulderPull(true);
        // Reaching out to the right and back drags the shoulder that way, within the bound, and not the idle one.
        Assert.True(cocked.X > 0.02f);
        Assert.True(cocked.Length() <= PlayerModel.MaxShoulderPullBlocks + 1e-4f);
        Assert.True(model.ShoulderPull(false).Length() < 0.01f);

        model.CancelThrowCharge();
        for (var i = 0; i < 180; i++) model.Update(Dt, 0f);
        Assert.True(model.ShoulderPull(true).Length() < 0.005f);
    }
}
