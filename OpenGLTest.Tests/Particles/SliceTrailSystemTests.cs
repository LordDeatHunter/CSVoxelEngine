using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>
/// The slice-trail component the world feeds: only a blade swing leaves a trail, a trail fades once
/// the swing is over, a remote's swing leaves one from the blade it is assumed to hold, and a player
/// who leaves takes their trail along.
/// </summary>
public sealed class SliceTrailSystemTests
{
    private const double Dt = 1.0 / 60.0;

    public SliceTrailSystemTests() => TestContent.EnsureLoaded();

    private static Item Blade => ItemRegistry.TryGet(ContentRegistry.RequireItem("obsidian_dagger").Id)!;
    private static Item Flint => ItemRegistry.TryGet(ContentRegistry.RequireItem("flint").Id)!;
    private static Item Dirt => ItemRegistry.TryGet(ContentRegistry.RequireBlock("dirt").Id)!;

    // Drives a local model and the system the way the world does: the models update, then the trails
    private sealed class Harness
    {
        public readonly SliceTrailSystem System = new();
        public readonly PlayerModel Local = new();
        public readonly List<RemotePlayer> Remotes = [];
        public readonly Vector3 Eye = new(3f, 20f, -7f);
        public Item? Held;
        public double Now = 10.0;

        public void Step()
        {
            Now += Dt;
            Local.Update((float)Dt, 0f);
            foreach (var remote in Remotes)
                remote.Update(Dt);
            System.Update(Now, Local, Held, Eye, -90f, 0f, 0f, Remotes);
        }

        public void Steps(int count)
        {
            for (var i = 0; i < count; i++)
                Step();
        }

        public void StepSeconds(double seconds) => Steps((int)(seconds / Dt) + 1);
    }

    private static RemotePlayer NewRemote(int id)
    {
        var remote = new RemotePlayer(id, "Other", PlayerRig.BuiltIn());
        remote.SetTarget(new Vector3(0f, 1.62f, 0f), -90f, 0f);
        for (var i = 0; i < 30; i++) remote.Update(Dt);
        return remote;
    }

    [Fact]
    public void APunchLeavesNoTrail()
    {
        foreach (var held in new[] { Flint, Dirt, null })
        {
            var h = new Harness { Held = held };
            h.Local.Punch();
            h.Steps(30);
            Assert.Empty(h.System.Active);

            // Even a slicing body draws no arc out of a hand that holds no blade
            h.Local.StartSlice(1);
            h.Steps(30);
            Assert.Empty(h.System.Active);
        }
    }

    [Fact]
    public void ABladeSliceLeavesATrailThatFades()
    {
        var h = new Harness { Held = Blade };
        h.Local.StartSlice(1);
        h.StepSeconds(0.3);
        Assert.True(h.Local.IsSlicing);
        Assert.NotEmpty(h.System.Active);
        Assert.Equal(Blade.TrailColorOrDefault, h.System.Active[0].Color);

        for (var i = 0; i < 10_000 && h.Local.IsSlicing; i++)
            h.Step();
        h.StepSeconds(ParticleConfig.SliceTrailLifeSeconds);
        Assert.Empty(h.System.Active);
    }

    [Fact]
    public void ARemoteSwordSwingLeavesATrail()
    {
        var h = new Harness();
        var remote = NewRemote(7);
        h.Remotes.Add(remote);
        h.Steps(10);
        Assert.Empty(h.System.Active);

        remote.SetTarget(remote.Eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.SwordCut, 1);
        h.StepSeconds(0.35);
        Assert.True(remote.Model.IsSwordSwinging);
        var trail = Assert.Single(h.System.Active);
        Assert.Equal(SliceTrailSystem.AssumedBlade(remote.Model)!.TrailColorOrDefault, trail.Color);
    }

    [Fact]
    public void ADepartedRemoteLosesItsTrail()
    {
        var h = new Harness();
        var remote = NewRemote(7);
        h.Remotes.Add(remote);
        h.Steps(10);
        remote.SetTarget(remote.Eye, -90f, 0f, NetPlayerFlags.None, NetPlayerAction.Slice, 1);
        h.StepSeconds(0.3);
        Assert.NotEmpty(h.System.Active);

        // The player leaves mid-swing: its arc goes with it rather than fading on in an empty spot
        h.Remotes.Clear();
        h.Step();
        Assert.Empty(h.System.Active);

        // Coming back under the same id starts from nothing
        var returned = NewRemote(7);
        h.Remotes.Add(returned);
        h.Step();
        Assert.Empty(h.System.Active);
    }

    [Fact]
    public void TheAssumedBladeMatchesTheSwing()
    {
        var model = new PlayerModel();
        Assert.Null(SliceTrailSystem.AssumedBlade(model));

        model.StartSlice(1);
        Assert.Equal(AttackPattern.Slice, SliceTrailSystem.AssumedBlade(model)!.Attack);

        foreach (var swing in new[] { SwordSwing.Cut, SwordSwing.BackCut, SwordSwing.Chop })
        {
            var sword = new PlayerModel();
            sword.StartSword(swing);
            Assert.Equal(AttackPattern.Sword, SliceTrailSystem.AssumedBlade(sword)!.Attack);
        }

        var punching = new PlayerModel();
        punching.Punch();
        Assert.Null(SliceTrailSystem.AssumedBlade(punching));
    }
}
