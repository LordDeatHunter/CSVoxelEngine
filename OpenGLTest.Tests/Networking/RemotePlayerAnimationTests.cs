using System;
using System.Numerics;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Networking;

public sealed class RemotePlayerAnimationTests
{
    private const float Dt = 1f / 60f;

    private static RemotePlayer NewRemote()
    {
        var remote = new RemotePlayer(1, "Other", PlayerRig.BuiltIn());
        remote.SetTarget(new Vector3(0f, 1.62f, 0f), -90f, 0f);
        for (var i = 0; i < 30; i++) remote.Update(Dt);
        return remote;
    }

    [Fact]
    public void ANewActionCounterPlaysTheSwingOnTheModelAndARepeatPlaysItAgain()
    {
        var remote = NewRemote();
        var eye = new Vector3(0f, 1.62f, 0f);
        Assert.Equal(1f, remote.Model.MineT);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Punch, 1);
        Assert.True(remote.Model.MineT < 1f);

        for (var i = 0; i < 60; i++) remote.Update(Dt);
        Assert.Equal(1f, remote.Model.MineT);

        // The same counter again is the same broadcast, not a new swing.
        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Punch, 1);
        Assert.Equal(1f, remote.Model.MineT);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Punch, 2);
        Assert.True(remote.Model.MineT < 1f);
    }

    [Fact]
    public void ASliceAndAThrowStartTheirOwnAnimations()
    {
        var remote = NewRemote();
        var eye = new Vector3(0f, 1.62f, 0f);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Slice, 1);
        Assert.True(remote.Model.IsSlicing);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Throw, 2);
        Assert.True(remote.Model.IsThrowing);
    }

    [Fact]
    public void AFirstSightingOnlyRecordsTheCounterInsteadOfSwinging()
    {
        var remote = new RemotePlayer(2, "Late", PlayerRig.BuiltIn());
        remote.SetTarget(new Vector3(0f, 1.62f, 0f), -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Punch, 7);

        Assert.Equal(1f, remote.Model.MineT);
    }

    [Fact]
    public void TheMovementFlagsPoseTheModel()
    {
        var remote = NewRemote();
        var eye = new Vector3(0f, 1.62f, 0f);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.Sneaking);
        for (var i = 0; i < 60; i++) remote.Update(Dt);
        Assert.True(remote.Model.CrouchBlend > 0.9f);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.Swimming);
        for (var i = 0; i < 90; i++) remote.Update(Dt);
        Assert.True(remote.Model.SwimBlend > 0.9f);
        Assert.True(remote.Model.CrouchBlend < 0.05f);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.Airborne);
        for (var i = 0; i < 30; i++) remote.Update(Dt);
        Assert.True(remote.Model.AirBlend > 0.9f);
    }

    [Fact]
    public void TheFloatingFlagPosesTheSurfaceFloatOverTheSwim()
    {
        var remote = NewRemote();
        var eye = new Vector3(0f, 1.62f, 0f);

        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.Swimming | NetPlayerFlags.Floating);
        for (var i = 0; i < 120; i++) remote.Update(Dt);
        Assert.True(remote.Model.SwimBlend > 0.9f);
        Assert.True(remote.Model.FloatBlend > 0.9f);

        // Under water (or from an older client that never sets the bit) it is the plain swim
        remote.SetTarget(eye, -90f, 0f, NetPlayerFlags.Swimming);
        for (var i = 0; i < 120; i++) remote.Update(Dt);
        Assert.True(remote.Model.SwimBlend > 0.9f);
        Assert.True(remote.Model.FloatBlend < 0.05f);
    }

    [Fact]
    public void ARemotePlayerRunsWhenTheFlagSaysSprintAndTheyMove()
    {
        var remote = NewRemote();
        var x = 0f;
        for (var i = 0; i < 120; i++)
        {
            x += 9f * Dt;
            remote.SetTarget(new Vector3(x, 1.62f, 0f), -90f, 0f, NetPlayerFlags.Sprinting);
            remote.Update(Dt);
        }

        Assert.True(remote.Model.RunBlend > 0.8f);
    }

    [Fact]
    public void ThePlayerListCarriesTheLastSwingAndACounterThatMovesOnEveryOne()
    {
        var player = new ServerPlayer(1, "A", Vector3.Zero);
        var seq0 = player.ActionSeq;

        player.NoteAction(NetPlayerAction.Punch);
        var seq1 = player.ActionSeq;
        player.NoteAction(NetPlayerAction.Punch);

        Assert.Equal(NetPlayerAction.Punch, player.LastAction);
        Assert.NotEqual(seq0, seq1);
        Assert.NotEqual(seq1, player.ActionSeq);
    }
}
