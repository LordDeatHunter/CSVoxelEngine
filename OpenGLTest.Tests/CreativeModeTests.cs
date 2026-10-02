using System.Numerics;
using Silk.NET.Input;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CreativeModeTests
{
    public CreativeModeTests() => TestContent.EnsureLoaded();

    private static (Camera Camera, ScriptedKeyboard Keys, ChunkManager Manager) Setup()
    {
        var manager = new ChunkManager(null!);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 0f, pitch: 0f);
        return (camera, new ScriptedKeyboard(), manager);
    }

    private static void Update(Camera camera, ScriptedKeyboard keys, ChunkManager manager, float dt = 0.05f)
        => camera.Update(dt, keys, manager);

    private static void Tap(Camera camera, ScriptedKeyboard keys, ChunkManager manager, Key key)
    {
        keys.Press([key]);
        Update(camera, keys, manager);
        keys.ReleaseAll();
        Update(camera, keys, manager);
    }

    private static void DoubleTapSpace(Camera camera, ScriptedKeyboard keys, ChunkManager manager)
    {
        keys.Press([Key.Space]);
        Update(camera, keys, manager);
        keys.ReleaseAll();
        Update(camera, keys, manager);
        keys.Press([Key.Space]);
        Update(camera, keys, manager);
        keys.ReleaseAll();
        Update(camera, keys, manager);
    }

    private static float WalkForward(Camera camera, ScriptedKeyboard keys, ChunkManager manager, bool sprint)
    {
        camera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 0f, pitch: 0f);
        keys.Press(sprint ? [Key.W, Key.ShiftLeft] : [Key.W]);
        for (var i = 0; i < 20; i++)
            Update(camera, keys, manager);
        keys.ReleaseAll();
        return camera.Position.X - 0.5f;
    }

    [Fact]
    public void DoubleTapSpaceDoesNothingOutsideCreative()
    {
        var (camera, keys, manager) = Setup();

        DoubleTapSpace(camera, keys, manager);

        Assert.False(camera.IsFlying);
    }

    [Fact]
    public void CreativeEnablesFlightAndLeavingCreativeGroundsThePlayer()
    {
        var (camera, keys, manager) = Setup();

        Tap(camera, keys, manager, Key.C);
        Assert.True(camera.IsCreative);
        Assert.False(camera.IsFlying);

        DoubleTapSpace(camera, keys, manager);
        Assert.True(camera.IsFlying);

        Tap(camera, keys, manager, Key.C);
        Assert.False(camera.IsCreative);
        Assert.False(camera.IsFlying);
    }

    [Fact]
    public void SprintIsSlightOnFootAndGigaWhileFlying()
    {
        var (camera, keys, manager) = Setup();

        var walk = WalkForward(camera, keys, manager, sprint: false);
        var sprint = WalkForward(camera, keys, manager, sprint: true);
        Assert.InRange(sprint / walk, 1.4, 1.6);

        Tap(camera, keys, manager, Key.C);
        DoubleTapSpace(camera, keys, manager);
        Assert.True(camera.IsFlying);

        camera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 0f, pitch: 0f);
        keys.Press([Key.W, Key.ShiftLeft]);
        for (var i = 0; i < 20; i++)
            Update(camera, keys, manager);
        keys.ReleaseAll();
        var flyBoost = camera.Position.X - 0.5f;

        Assert.InRange(flyBoost / walk, 9.0, 11.0);
    }

    [Fact]
    public void CreativeTreeFellDropsNothingButSurvivalDrops()
    {
        var tree = new TopplingTree(
            [new TopplingTree.FellBlock(new Vector3(0, 1, 0), ContentRegistry.RequireBlock("stone").Id, 0)],
            new Vector3(0.5f, 1f, 0.5f), Vector3.UnitX, drops: false);
        Assert.Empty(tree.TakeDrops());
        tree.Dispose();

        var normal = new TopplingTree(
            [new TopplingTree.FellBlock(new Vector3(0, 1, 0), ContentRegistry.RequireBlock("stone").Id, 0)],
            new Vector3(0.5f, 1f, 0.5f), Vector3.UnitX);
        Assert.NotEmpty(normal.TakeDrops());
        normal.Dispose();
    }
}
