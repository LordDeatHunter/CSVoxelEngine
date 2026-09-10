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
    public void PaletteHasOneOpaqueSwatchPerSlot()
    {
        var pixels = PlayerModel.PalettePixels();
        Assert.Equal(PlayerModel.PaletteCols * PlayerModel.PaletteRows * 4, pixels.Length);
        for (var i = 0; i < PlayerModel.PaletteCols * PlayerModel.PaletteRows; i++)
            Assert.Equal(255, pixels[i * 4 + 3]);
    }
}
