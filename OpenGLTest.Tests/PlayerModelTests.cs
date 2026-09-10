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
    public void AirtimeStaysAWalkPoseEvenWhenSprinting()
    {
        var player = new PlayerModel();
        for (var i = 0; i < 180; i++)
            player.Update(1f / 60f, 12f, sprinting: true, grounded: false);
        Assert.True(player.RunBlend < 0.1f);
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
    public void BodyRootedAtFeetBelowTheEyeFacingCameraYaw()
    {
        var player = new PlayerModel();
        var eye = new Vector3(0.5f, 70f, 0.5f);
        var torso = player.GetPartMatrix("Torso", eye, -90f, 0f);
        var feet = eye - new Vector3(0f, Camera.EyeHeight, 0f);
        // Torso origin sits at hip height above the feet.
        var hip = Vector3.Transform(Vector3.Zero, torso);
        Assert.Equal(feet.X, hip.X, 3);
        Assert.Equal(feet.Y + PlayerModel.HipY, hip.Y, 3);
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
        Assert.True(PlayerModel.FirstPersonBodyBack > PlayerModel.TorsoDepth * 0.5f);
    }

    [Fact]
    public void HandHangsBelowTheEyeOnTheRightSide()
    {
        var player = new PlayerModel();
        Assert.Equal(-PlayerModel.ForearmLength, player.HandDepth());

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
        var pixels = PlayerModel.PalettePixels();
        Assert.Equal(PlayerModel.PaletteCols * PlayerModel.PaletteRows * 4, pixels.Length);
        for (var i = 0; i < PlayerModel.PaletteCols * PlayerModel.PaletteRows; i++)
            Assert.Equal(255, pixels[i * 4 + 3]);
    }
}
