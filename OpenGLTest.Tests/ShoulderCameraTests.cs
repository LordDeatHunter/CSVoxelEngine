using System.Numerics;
using Silk.NET.Input;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ShoulderCameraTests
{
    public ShoulderCameraTests() => TestContent.EnsureLoaded();

    private static (Camera Camera, ScriptedKeyboard Keys, ChunkManager Manager, Chunk Center) Setup(
        Vector3 position)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var camera = new Camera(null, null);
        camera.TestTeleport(position, yaw: 0f, pitch: 0f);
        return (camera, new ScriptedKeyboard(), manager, center);
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

    private static void BuildWall(Chunk center, int x)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, y, z, stone, 0);
    }

    [Fact]
    public void F5TogglesShoulderModeAndRenderEyeStaysAtHeadInFirstPerson()
    {
        var (camera, keys, manager, _) = Setup(new Vector3(0.5f, 5.5f, 0.5f));

        Assert.False(camera.IsShoulderSurf);
        Assert.Equal(camera.Position, camera.RenderEye);

        Tap(camera, keys, manager, Key.F5);
        Assert.True(camera.IsShoulderSurf);

        Tap(camera, keys, manager, Key.F5);
        Assert.False(camera.IsShoulderSurf);
        Assert.Equal(camera.Position, camera.RenderEye);
    }

    [Fact]
    public void ShoulderCameraSitsBehindAboveAndToTheSideConvergingOnAim()
    {
        var (camera, keys, manager, _) = Setup(new Vector3(0.5f, 5.5f, 0.5f));

        Tap(camera, keys, manager, Key.F5);

        var offset = camera.RenderEye - camera.Position;
        Assert.True(offset.X < -2f, $"offset.X {offset.X} should reach behind (+X facing)");
        Assert.True(offset.Y > 0.3f, $"offset.Y {offset.Y} should sit above the head");
        Assert.True(offset.Z > 0.3f, $"offset.Z {offset.Z} should favour the right shoulder");

        var facing = camera.Forward;
        var alignment = Vector3.Dot(camera.ViewDirection, facing);
        Assert.True(alignment > 0.99f, $"view {alignment} should nearly match the head facing");
    }

    private static void BuildFloor(Chunk center, int y)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, y, z, stone, 0);
    }

    [Fact]
    public void ShoulderRaycastHitsTheBlockUnderTheCrosshair()
    {
        var (camera, keys, manager, center) = Setup(new Vector3(0.5f, 5.5f, 0.5f));
        BuildWall(center, x: 4);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(4f, 5f, 0f), fpHit);

        Tap(camera, keys, manager, Key.F5);

        Assert.True(camera.Raycast(10f, manager, out var shoulderHit, out _));
        Assert.Equal(fpHit, shoulderHit);
    }

    [Fact]
    public void ShoulderRaycastReachesAsFarFromThePlayerAsFirstPerson()
    {
        var (camera, keys, manager, center) = Setup(new Vector3(0.5f, 5.5f, 0.5f));
        BuildWall(center, x: 7);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(7f, 5f, 0f), fpHit);

        Tap(camera, keys, manager, Key.F5);

        Assert.True(camera.Raycast(10f, manager, out var shoulderHit, out _));
        Assert.Equal(fpHit, shoulderHit);
    }

    [Fact]
    public void ShoulderRaycastReachesDiagonalTargetsAsFarAsFirstPerson()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 45f, pitch: -45f);
        var keys = new ScriptedKeyboard();
        BuildFloor(center, y: 1);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(2f, 1f, 2f), fpHit);

        keys.Press([Key.F5]);
        camera.Update(0.05f, keys, manager);
        keys.ReleaseAll();
        camera.Update(0.05f, keys, manager);

        Assert.True(camera.IsShoulderSurf);
        Assert.True(camera.Raycast(10f, manager, out var shoulderHit, out _));
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.Equal(stone, manager.GetBlockGlobal(
            (int)shoulderHit.X, (int)shoulderHit.Y, (int)shoulderHit.Z));
        var headDistance = (shoulderHit + new Vector3(0.5f) - camera.Position).Length();
        Assert.True(headDistance <= 10.5f, $"hit {shoulderHit} is within head reach");
    }

    [Fact]
    public void ShoulderRaycastDoesNotExceedFirstPersonReach()
    {
        var (camera, keys, manager, center) = Setup(new Vector3(0.5f, 5.5f, 0.5f));
        BuildWall(center, x: 11);

        Assert.False(camera.Raycast(10f, manager, out _, out _));

        Tap(camera, keys, manager, Key.F5);
        Assert.True(camera.IsShoulderSurf);
        Assert.False(camera.Raycast(10f, manager, out _, out _));
    }

    [Fact]
    public void ShoulderRaycastRefusesABlockHiddenFromTheHeadBehindAPillar()
    {
        var (camera, keys, manager, center) = Setup(new Vector3(0.5f, 5.5f, 0.5f));
        var stone = ContentRegistry.RequireBlock("stone").Id;
        BuildWall(center, x: 4);

        Tap(camera, keys, manager, Key.F5);
        Assert.True(camera.IsShoulderSurf);

        // Sanity: with open sightlines the wall is targeted, so a later miss comes from
        // the head validation and not from a camera ray that never got there.
        Assert.True(camera.Raycast(10f, manager, out var openHit, out _));
        Assert.Equal(new Vector3(4f, 5f, 0f), openHit);

        // A pillar on the head's line but off the camera's: the camera still sees the wall
        // past it, yet the head cannot, so nothing may be mined or placed through it.
        center.SetBlockRaw(2, 5, 0, stone, 0);
        Assert.False(camera.Raycast(10f, manager, out _, out _));

        // ...while first person honestly reports the pillar itself.
        camera.TestTeleport(new Vector3(0.5f, 5.5f, 0.5f), yaw: 0f, pitch: 0f);
        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(2f, 5f, 0f), fpHit);
    }

    [Fact]
    public void ShoulderCameraPullsInFrontOfAWallBehindThePlayer()
    {
        var (camera, keys, manager, center) = Setup(new Vector3(8.5f, 5.5f, 8.5f));
        BuildWall(center, x: 5);

        Tap(camera, keys, manager, Key.F5);

        var offsetLength = (camera.RenderEye - camera.Position).Length();
        Assert.True(offsetLength < 3f, $"offset {offsetLength} should be pulled in short of the wall");
        Assert.True(offsetLength > 0.2f, "camera should not collapse all the way into the head");

        var eye = camera.RenderEye;
        Assert.Equal(0, manager.GetBlockGlobal(
            (int)float.Floor(eye.X), (int)float.Floor(eye.Y), (int)float.Floor(eye.Z)));
    }
}
