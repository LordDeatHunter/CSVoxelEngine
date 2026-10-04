using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class PlayerModelTests
{
    public PlayerModelTests() => TestContent.EnsureLoaded();

    [Fact]
    public void StandingStillKeepsLimbsAtRest()
    {
        var player = new PlayerModel();
        player.Update(1f, 0f);
        var pose = player.GetPose();
        Assert.Equal(0f, pose.LeftHip, 3);
        Assert.True(MathF.Abs(pose.RightShoulder - PlayerModel.RestShoulderForward) < 0.03f);
        Assert.True(pose.RightElbow >= PlayerModel.RestElbow);
        Assert.Equal(0f, pose.Bob, 3);
        Assert.Equal(1f, player.MineT);
    }

    [Fact]
    public void WalkingSwingsOppositeLimbsWithKneeAndElbowFlex()
    {
        var player = new PlayerModel();
        for (var i = 0; i < 60; i++)
            player.Update(1f / 60f, 8f);

        var pose = player.GetPose();
        Assert.True(player.MoveBlend > 0.9f);
        // Hips counter-swing; shoulders counter-swing about the forward rest pose.
        Assert.Equal(pose.LeftHip, -pose.RightHip, 3);
        Assert.Equal(pose.LeftShoulder - PlayerModel.RestShoulderForward,
            -(pose.RightShoulder - PlayerModel.RestShoulderForward), 2);
        // Two segments: knees bend backward (negative), elbows stay flexed forward.
        Assert.True(pose.LeftKnee <= 0f && pose.RightKnee <= 0f);
        Assert.True(pose.LeftElbow > 0f && pose.RightElbow > 0f);
        // Swing stays within a sane range.
        Assert.True(MathF.Abs(pose.LeftHip) <= 0.7f);
    }

    [Fact]
    public void SprintUsesALargerCycleThanWalking()
    {
        var walk = new PlayerModel();
        var walkPeak = 0f;
        for (var i = 0; i < 180; i++)
        {
            walk.Update(1f / 60f, 8f, sprinting: false, grounded: true);
            walkPeak = MathF.Max(walkPeak, MathF.Abs(walk.GetPose().LeftHip));
        }

        var run = new PlayerModel();
        var runPeak = 0f;
        for (var i = 0; i < 180; i++)
        {
            run.Update(1f / 60f, 12f, sprinting: true, grounded: true);
            runPeak = MathF.Max(runPeak, MathF.Abs(run.GetPose().LeftHip));
        }

        Assert.True(walk.RunBlend < 0.1f);
        Assert.True(run.RunBlend > 0.9f);
        Assert.True(runPeak > walkPeak + 0.15f);
        Assert.True(run.GetPose().TorsoPitch < walk.GetPose().TorsoPitch - 0.05f);
    }

    [Fact]
    public void AirtimeUsesAJumpPoseInsteadOfTheRunCycle()
    {
        var player = new PlayerModel();
        for (var i = 0; i < 180; i++)
            player.Update(1f / 60f, 12f, sprinting: true, grounded: false, verticalVelocity: 5f);
        Assert.True(player.RunBlend < 0.1f);
        Assert.True(player.AirBlend > 0.9f);
        var pose = player.GetPose();
        Assert.True(pose.LeftHip > 0.3f);
        Assert.True(pose.RightHip < -0.2f);
        Assert.True(pose.LeftKnee < -0.2f && pose.RightKnee < -0.2f);
        Assert.Equal(0f, pose.Bob, 3);
    }

    [Fact]
    public void SprintJumpSplitsWiderThanAStandingJump()
    {
        var hop = new PlayerModel();
        for (var i = 0; i < 180; i++)
            hop.Update(1f / 60f, 0f, sprinting: false, grounded: false, verticalVelocity: 5f);
        var leap = new PlayerModel();
        for (var i = 0; i < 180; i++)
            leap.Update(1f / 60f, 12f, sprinting: true, grounded: false, verticalVelocity: 5f);

        var hopPose = hop.GetPose();
        var leapPose = leap.GetPose();
        Assert.True(leapPose.LeftHip - leapPose.RightHip > hopPose.LeftHip - hopPose.RightHip + 0.3f);
        Assert.True(leapPose.TorsoPitch < hopPose.TorsoPitch - 0.1f);
    }

    [Fact]
    public void FallingExtendsTheLegsTowardLanding()
    {
        var rising = new PlayerModel();
        for (var i = 0; i < 180; i++)
            rising.Update(1f / 60f, 0f, sprinting: false, grounded: false, verticalVelocity: 10f);
        var falling = new PlayerModel();
        for (var i = 0; i < 180; i++)
            falling.Update(1f / 60f, 0f, sprinting: false, grounded: false, verticalVelocity: -12f);

        Assert.True(rising.GetPose().LeftKnee < falling.GetPose().LeftKnee - 0.15f);
    }

    [Fact]
    public void PunchSwingsTheRightArmAndDecays()
    {
        var player = new PlayerModel();
        player.Update(1f / 60f, 0f);
        var rest = player.GetPose().RightShoulder;

        player.Punch();
        Assert.Equal(0f, player.MineT);
        player.Update(0.125f, 0f);
        var mid = player.GetPose().RightShoulder;
        Assert.True(mid > rest + 0.5f);

        for (var i = 0; i < 60; i++)
            player.Update(1f / 60f, 0f);
        Assert.Equal(1f, player.MineT);
        Assert.True(MathF.Abs(player.GetPose().RightShoulder - rest) < 0.05f);
    }

    [Fact]
    public void PunchSwingsTheRightArmWhileAirborne()
    {
        var player = new PlayerModel();
        for (var i = 0; i < 180; i++)
            player.Update(1f / 60f, 0f, sprinting: false, grounded: false, verticalVelocity: -12f);
        Assert.True(player.AirBlend > 0.9f);
        var airborneRest = player.GetPose().RightShoulder;

        player.Punch();
        player.Update(0.125f, 0f, sprinting: false, grounded: false, verticalVelocity: -12f);
        var airborneSwing = player.GetPose().RightShoulder;
        Assert.True(airborneSwing > airborneRest + 0.5f);
    }

    [Fact]
    public void PunchDrivesTheFistOntoTheAim()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        var aim = new Vector3(0f, 0f, -1f);
        // The first-person framing the jab is solved for; the shadow pass draws the same joints
        // from the true feet instead.
        var draw = PlayerModel.FirstPersonBodyBack;
        Vector3 Fist() => Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f, draw));
        // Angle between the aim and the direction to the fist: 0 is dead on the crosshair.
        float OffAim() => MathF.Acos(Vector3.Dot(Vector3.Normalize(Fist() - eye), aim));

        player.Punch();
        player.Update(PlayerModel.MineDuration * 0.22f, 0f);
        var chamberOffAim = OffAim();
        var chamberElbow = player.GetPose().RightElbow;
        player.Update(PlayerModel.MineDuration * 0.33f, 0f);
        var strikeOffAim = OffAim();
        var strikeElbow = player.GetPose().RightElbow;

        // The jab closes most of the angle the chambered fist sits off the crosshair instead of
        // staying out on the shoulder's line, and it gets there by straightening the elbow.
        Assert.True(chamberOffAim > 0.3f);
        Assert.True(strikeOffAim < chamberOffAim * 0.5f);
        Assert.True(strikeElbow < chamberElbow - 1f);
        Assert.True(Fist().Y <= eye.Y);
    }

    [Fact]
    public void PunchRisesToTheAimLineWithoutPassingAboveIt()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        float FistAboveEye() => Vector3.Transform(Vector3.Zero,
            player.GetHandAnchorMatrix(eye, -90f, PlayerModel.FirstPersonBodyBack)).Y - eye.Y;

        player.Punch();
        var highest = FistAboveEye();
        for (var i = 0; i < 25; i++)
        {
            player.Update(PlayerModel.MineDuration / 25f, 0f);
            highest = MathF.Max(highest, FistAboveEye());
        }

        // An upward swing rode the fist over the face; the jab tops out on the aim line, which
        // keeps the crosshair itself clear.
        Assert.True(highest <= 0f);
    }

    [Fact]
    public void PunchFollowsTheAimWhenLookingUp()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        // Angle between the aim and the direction to the fist at the impact, for a look pitch.
        float OffAimAtImpact(float pitchDeg)
        {
            player.Punch();
            player.Update(PlayerModel.MineDuration * 0.55f, 0f);
            var fist = Vector3.Transform(Vector3.Zero,
                player.GetHandAnchorMatrix(eye, -90f, PlayerModel.FirstPersonBodyBack, pitchDeg));
            var p = pitchDeg * MathF.PI / 180f;
            return MathF.Acos(Vector3.Dot(Vector3.Normalize(fist - eye),
                new Vector3(0f, MathF.Sin(p), -MathF.Cos(p))));
        }

        var level = OffAimAtImpact(0f);
        var overhead = OffAimAtImpact(85f);

        // The jab is solved against the view, not the body, so looking up sends the fist up
        // along the new aim instead of leaving it punching out where the body faces.
        Assert.True(level < 0.4f, $"level off-aim {level}");
        Assert.True(MathF.Abs(overhead - level) < 0.15f, $"level {level} overhead {overhead}");
    }

    [Fact]
    public void BodyRootedAtFeetBelowTheEyeFacingCameraYaw()
    {
        var player = new PlayerModel();
        var eye = new Vector3(0.5f, 70f, 0.5f);
        var torso = player.GetPartMatrix("Torso", eye, -90f, 0f);
        var feet = eye - new Vector3(0f, Camera.EyeHeight, 0f);
        // Torso origin sits at hip height above the feet.
        var hip = Vector3.Transform(Vector3.Zero, torso);
        Assert.Equal(feet.X, hip.X, 3);
        Assert.Equal(feet.Y + PlayerRig.HipY, hip.Y, 3);
        Assert.Equal(feet.Z, hip.Z, 3);
    }

    [Fact]
    public void FirstPersonShowsTorsoButNotHead()
    {
        Assert.Contains("Torso", PlayerModel.FirstPersonParts);
        Assert.DoesNotContain("Head", PlayerModel.FirstPersonParts);
        Assert.Contains("UpperArmL", PlayerModel.FirstPersonParts);
        Assert.Contains("ShinR", PlayerModel.FirstPersonParts);
    }

    [Fact]
    public void BackOffsetSlidesTheBodyAwayFromFacing()
    {
        var player = new PlayerModel();
        var eye = new Vector3(0.5f, 70f, 0.5f);
        // Yaw -90 faces -Z, so "back" is +Z.
        var shifted = player.GetPartMatrix("Torso", eye, -90f, 0f, PlayerModel.FirstPersonBodyBack);
        var hip = Vector3.Transform(Vector3.Zero, shifted);
        Assert.Equal(eye.X, hip.X, 3);
        Assert.Equal(eye.Z + PlayerModel.FirstPersonBodyBack, hip.Z, 3);
        Assert.True(PlayerModel.FirstPersonBodyBack > PlayerRig.TorsoDepth * 0.5f);
    }

    [Fact]
    public void HandHangsBelowTheEyeOnTheRightSide()
    {
        var player = new PlayerModel();
        Assert.Equal(-PlayerRig.ForearmLength, player.HandDepth());

        // Yaw -90 faces -Z, so the right hand sits at +X and forward at -Z.
        var eye = new Vector3(0.5f, 70f, 0.5f);
        var hand = Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f));
        Assert.True(hand.Y < eye.Y - 0.5f);
        Assert.True(hand.X > eye.X);
        Assert.True(hand.Z < eye.Z);
    }

    [Fact]
    public void HoldingRaisesTheRightHandAndReleasesIt()
    {
        var player = new PlayerModel();
        var eye = new Vector3(0.5f, 70f, 0.5f);
        var rest = Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f));

        player.HoldingItem = true;
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);
        Assert.Equal(1f, player.HoldBlend, 3);
        var raised = Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f));
        Assert.True(raised.Y > rest.Y + 0.2f);
        Assert.True(raised.Z < rest.Z - 0.1f);

        player.HoldingItem = false;
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);
        Assert.Equal(0f, player.HoldBlend, 3);
        var lowered = Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f));
        Assert.True(Vector3.Distance(rest, lowered) < 0.05f);
    }

    [Fact]
    public void PaletteHasOneOpaqueSwatchPerSlot()
    {
        var pixels = PlayerRig.PalettePixels();
        Assert.Equal(PlayerRig.PaletteCols * PlayerRig.PaletteRows * 4, pixels.Length);
        for (var i = 0; i < PlayerRig.PaletteCols * PlayerRig.PaletteRows; i++)
            Assert.Equal(255, pixels[i * 4 + 3]);
    }

    [Fact]
    public void SliceSwingFinishesAfterItsDuration()
    {
        var player = new PlayerModel();
        player.StartSlice(1);
        Assert.True(player.IsSlicing);
        Assert.Equal(0f, player.SliceProgress, 3);

        for (var i = 0; i < 60; i++)
            player.Update(1f / 60f, 0f);

        Assert.False(player.IsSlicing);
        Assert.Equal(1f, player.SliceProgress, 3);
    }

    [Fact]
    public void SliceSweepWindsUpStrikesLeftAndSettles()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        float HandX() => Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f)).X;

        player.StartSlice(1);
        var rest = HandX();

        player.Update(PlayerModel.SliceDuration * 0.15f, 0f); // peak of the wind-up
        var windup = HandX();

        player.Update(PlayerModel.SliceDuration * 0.45f, 0f); // end of the strike
        var followThrough = HandX();

        player.Update(PlayerModel.SliceDuration * 0.4f, 0f);  // settled back to the idle pose
        var settled = HandX();

        // Right-to-left, against the body centre: wound up to the player's right, struck to the left.
        Assert.True(windup > eye.X);
        Assert.True(followThrough < eye.X);
        Assert.True(windup > followThrough);
        // No teleport: the swing ends at the pose it started from.
        Assert.True(MathF.Abs(settled - rest) < 0.05f);
    }

    [Fact]
    public void SliceReturnMirrorsTheSweep()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        float HandX() => Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f)).X;

        player.StartSlice(-1);
        player.Update(PlayerModel.SliceDuration * 0.15f, 0f);
        var windup = HandX();

        player.Update(PlayerModel.SliceDuration * 0.45f, 0f);
        var followThrough = HandX();

        // The return mirrors the first swing: wound up to the left, struck through to the right.
        Assert.True(windup < eye.X);
        Assert.True(followThrough > eye.X);
    }

    [Fact]
    public void SliceRollsTheBladeFlatAndFlipsItForTheReturn()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);

        // The blade's up axis measured on the body's right axis: 0 upright at rest, rolling out
        // to the side as the wrist lays it flat.
        float BladeOutward() =>
            Vector3.TransformNormal(Vector3.UnitY, player.GetHandAnchorMatrix(eye, -90f)).X;

        player.StartSlice(1);
        var rest = BladeOutward();
        player.Update(PlayerModel.SliceDuration * PlayerModel.SliceImpactProgress, 0f);
        var rightToLeft = BladeOutward();

        // The return rolls the other way, so the blade lays out on the opposite side.
        player.StartSlice(-1);
        player.Update(PlayerModel.SliceDuration * PlayerModel.SliceImpactProgress, 0f);
        var leftToRight = BladeOutward();

        Assert.True(rest < 0.3f);
        Assert.True(rightToLeft > 0.7f);
        Assert.True(leftToRight < -0.7f);
    }

    [Fact]
    public void SliceLowersTheHandToChestAtTheStrike()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        float HandY() => Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f)).Y;

        player.StartSlice(1);
        var rest = HandY();
        player.Update(PlayerModel.SliceDuration * PlayerModel.SliceImpactProgress, 0f);
        var strike = HandY();

        // The drop brings the strike down off the eye line, back near the idle holding height.
        Assert.True(strike < eye.Y - 0.25f);
        Assert.True(MathF.Abs(strike - rest) < 0.12f);
    }

    [Fact]
    public void SliceKeepsTheShoulderAnchored()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);

        var eye = new Vector3(0.5f, 70f, 0.5f);
        float ShoulderY() => player.GetPartMatrix("UpperArmR", eye, -90f, 0f).Translation.Y;

        player.StartSlice(1);
        var rest = ShoulderY();
        player.Update(PlayerModel.SliceDuration * PlayerModel.SliceImpactProgress, 0f);
        var strike = ShoulderY();

        // The hand is lowered by re-posing the arm, so the shoulder joint itself never moves and
        // stays attached to the torso.
        Assert.Equal(rest, strike, 4);
    }

    [Fact]
    public void ThrowChargeRaisesTheRightArmOverheadProgressively()
    {
        var player = new PlayerModel { HoldingItem = true };
        for (var i = 0; i < 120; i++)
            player.Update(1f / 60f, 0f);
        var rest = player.GetPose().RightShoulder;
        var restElbow = player.GetPose().RightElbow;
        var eye = new Vector3(0.5f, 70f, 0.5f);
        float HandY() => Vector3.Transform(Vector3.Zero, player.GetHandAnchorMatrix(eye, -90f)).Y;
        var restHand = HandY();

        player.StartThrowCharge();
        player.UpdateThrowCharge(0f);
        var tapped = player.GetPose().RightShoulder;
        var midHand = 0f;
        player.UpdateThrowCharge(0.5f);
        var half = player.GetPose().RightShoulder;
        midHand = HandY();
        player.UpdateThrowCharge(1f);
        var full = player.GetPose().RightShoulder;

        Assert.True(tapped >= rest);
        Assert.True(half > tapped);
        Assert.True(full > half);
        Assert.True(full > rest + 0.5f);
        Assert.True(player.GetPose().RightElbow < restElbow);
        Assert.True(midHand > restHand);
        Assert.True(HandY() > restHand + 0.15f);
    }

    [Fact]
    public void ThrowReleaseFallsToRestWithoutBouncing()
    {
        var player = new PlayerModel();
        player.Update(1f / 60f, 0f);
        var rest = player.GetPose().RightShoulder;

        player.StartThrowCharge();
        player.UpdateThrowCharge(1f);
        var cocked = player.GetPose().RightShoulder;
        player.ReleaseThrow(1f);
        Assert.True(player.IsThrowing);
        Assert.Equal(0f, player.ThrowProgress, 3);
        Assert.Equal(cocked, player.GetPose().RightShoulder, 4);

        var last = cocked;
        for (var i = 1; i <= 4; i++)
        {
            player.Update(PlayerModel.ThrowDuration / 4f, 0f);
            var sampled = player.GetPose().RightShoulder;
            Assert.True(sampled < last - 0.05f);
            last = sampled;
        }

        for (var i = 0; i < 60; i++)
            player.Update(1f / 60f, 0f);
        Assert.False(player.IsThrowing);
        Assert.True(MathF.Abs(player.GetPose().RightShoulder - rest) < 0.05f);
    }

    [Fact]
    public void ThrowCancelDropsTheCockedArm()
    {
        var player = new PlayerModel();
        player.Update(1f / 60f, 0f);
        var rest = player.GetPose().RightShoulder;

        player.StartThrowCharge();
        player.UpdateThrowCharge(1f);
        Assert.True(player.GetPose().RightShoulder > rest + 0.5f);

        player.CancelThrowCharge();
        Assert.True(MathF.Abs(player.GetPose().RightShoulder - rest) < 0.05f);
        Assert.False(player.IsThrowing);
    }

    [Fact]
    public void ThrowReleaseCarriesItsChargeExplicitly()
    {
        var player = new PlayerModel();
        player.Update(1f / 60f, 0f);
        var rest = player.GetPose().RightShoulder;

        player.StartThrowCharge();
        player.UpdateThrowCharge(1f);
        var cocked = player.GetPose().RightShoulder;
        player.CancelThrowCharge();
        player.ReleaseThrow(1f);

        Assert.True(player.IsThrowing);
        Assert.Equal(cocked, player.GetPose().RightShoulder, 4);
        Assert.True(player.GetPose().RightShoulder > rest + 0.5f);
    }
}
