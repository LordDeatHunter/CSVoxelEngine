using System.Numerics;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

public sealed class FirstPersonReachTests
{
    public FirstPersonReachTests() => TestContent.EnsureLoaded();

    private static (ChunkManager Manager, Chunk Center) Setup() => MeshTestWorld.WalledArea();

    [Fact]
    public void DiagonalTargetWithinDistanceReachIsNotLimitedByDdaCellCount()
    {
        var (manager, center) = Setup();
        var grass = ContentRegistry.RequireBlock("grass").Id;
        center.SetBlockRaw(1, 11, 1, grass, 0);

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(5.5f, 16.5f, 7.5f), yaw: -123.6901f, pitch: -31.9657f);

        var hit = camera.Raycast(10f, manager, out var hitPosition, out _);

        Assert.True(hit, "A block intersected before ten traveled blocks should be targetable even when the DDA crosses more than ten cells.");
        Assert.Equal(new Vector3(1f, 11f, 1f), hitPosition);
    }

    [Fact]
    public void FirstPersonReachStillRejectsBlocksBeyondDistance()
    {
        var (manager, center) = Setup();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        center.SetBlockRaw(11, 5, 0, stone, 0);

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 0f, pitch: 0f);

        Assert.False(camera.Raycast(10f, manager, out _, out _));
    }

    [Fact]
    public void FirstPersonReachKeepsCloseAndNegativeCoordinateTargets()
    {
        var (manager, center) = Setup();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        center.SetBlockRaw(4, 5, 0, stone, 0);
        manager.Chunks[new Vector3(0f, 0f, -1f)].SetBlockRaw(1, 5, 15, stone, 0);

        var closeCamera = new Camera(null, null);
        closeCamera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 0f, pitch: 0f);
        Assert.True(closeCamera.Raycast(10f, manager, out var closeHit, out _));
        Assert.Equal(new Vector3(4f, 5f, 0f), closeHit);

        var negativeCamera = new Camera(null, null);
        negativeCamera.TestTeleport(new Vector3(-3.5f, 5.5f, -0.5f), yaw: 0f, pitch: 0f);
        Assert.True(negativeCamera.Raycast(10f, manager, out var negativeHit, out _));
        Assert.Equal(new Vector3(1f, 5f, -1f), negativeHit);
    }
}
