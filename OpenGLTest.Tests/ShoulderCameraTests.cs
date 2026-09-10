using System;
using System.Numerics;
using Silk.NET.Input;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ShoulderCameraTests
{
    public ShoulderCameraTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 HeadStart = new(0.5f, 5.5f, 0.5f);

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

    // Runs the F5 glide to its endpoint (0.5 s). Headless heads fall while settling, so
    // callers re-teleport to the scenario start before asserting on geometry.
    private static void Settle(Camera camera, ScriptedKeyboard keys, ChunkManager manager, int frames = 14)
    {
        for (var i = 0; i < frames; i++)
            Update(camera, keys, manager);
    }

    private static void BuildWall(Chunk center, int x)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, y, z, stone, 0);
    }

    private static void BuildFloor(Chunk center, int y)
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, y, z, stone, 0);
    }

    [Fact]
    public void F5CyclesFirstPersonShoulderOrbitAndBack()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Assert.Equal(CameraMode.FirstPerson, camera.Mode);
        Assert.False(camera.IsShoulderSurf);

        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.Shoulder, camera.Mode);
        Assert.True(camera.IsShoulderSurf);
        Assert.False(camera.IsOrbit);

        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.Orbit, camera.Mode);
        Assert.True(camera.IsShoulderSurf);
        Assert.True(camera.IsOrbit);

        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.FirstPerson, camera.Mode);
        Assert.False(camera.IsShoulderSurf);
    }

    [Fact]
    public void F5GlidesCameraAndHeadOverHalfASecond()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Assert.False(camera.IsShoulderSurf);
        Assert.Equal(0f, camera.ShoulderBlend);
        Assert.Equal(camera.Position, camera.RenderEye);

        // 0.1 s into the 0.5 s glide: SmoothStep(0.2) is about 0.104, camera partway out.
        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.Shoulder, camera.Mode);
        Assert.InRange(camera.ShoulderBlend, 0.09f, 0.12f);
        Assert.InRange((camera.RenderEye - camera.Position).Length(), 0.3f, 0.5f);

        Settle(camera, keys, manager);
        Assert.Equal(1f, camera.ShoulderBlend);

        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.Orbit, camera.Mode);
        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.FirstPerson, camera.Mode);
        Assert.InRange(camera.ShoulderBlend, 0.88f, 0.91f);

        Settle(camera, keys, manager);
        Assert.Equal(0f, camera.ShoulderBlend);
        Assert.Equal(camera.Position, camera.RenderEye);
    }

    [Fact]
    public void ShoulderCameraSitsBehindAboveAndToTheSideConvergingOnAim()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        var offset = camera.RenderEye - camera.Position;
        Assert.True(offset.X < -2f, $"offset.X {offset.X} should reach behind (+X facing)");
        Assert.True(offset.Y > 0.3f, $"offset.Y {offset.Y} should sit above the head");
        Assert.True(offset.Z > 0.3f, $"offset.Z {offset.Z} should favour the right shoulder");

        var facing = camera.Forward;
        var alignment = Vector3.Dot(camera.ViewDirection, facing);
        Assert.True(alignment > 0.99f, $"view {alignment} should nearly match the head facing");
    }

    [Fact]
    public void ShoulderRaycastHitsTheBlockUnderTheCrosshair()
    {
        var (camera, keys, manager, center) = Setup(HeadStart);
        BuildWall(center, x: 4);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(4f, 5f, 0f), fpHit);

        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        Assert.True(camera.Raycast(10f, manager, out var shoulderHit, out _));
        Assert.Equal(fpHit, shoulderHit);
    }

    [Fact]
    public void ShoulderRaycastReachesAsFarFromThePlayerAsFirstPerson()
    {
        var (camera, keys, manager, center) = Setup(HeadStart);
        BuildWall(center, x: 7);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(7f, 5f, 0f), fpHit);

        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        Assert.True(camera.Raycast(10f, manager, out var shoulderHit, out _));
        Assert.Equal(fpHit, shoulderHit);
    }

    [Fact]
    public void ShoulderRaycastReachesDiagonalTargetsAsFarAsFirstPerson()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var camera = new Camera(null, null);
        camera.TestTeleport(HeadStart, yaw: 45f, pitch: -45f);
        var keys = new ScriptedKeyboard();
        BuildFloor(center, y: 1);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(2f, 1f, 2f), fpHit);

        keys.Press([Key.F5]);
        Update(camera, keys, manager);
        keys.ReleaseAll();
        Update(camera, keys, manager);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 45f, pitch: -45f);
        Update(camera, keys, manager);

        Assert.True(camera.IsShoulderSurf);
        Assert.Equal(1f, camera.ShoulderBlend);
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
        var (camera, keys, manager, center) = Setup(HeadStart);
        BuildWall(center, x: 11);

        Assert.False(camera.Raycast(10f, manager, out _, out _));

        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        Assert.True(camera.IsShoulderSurf);
        Assert.False(camera.Raycast(10f, manager, out _, out _));
    }

    [Fact]
    public void ShoulderRaycastRefusesABlockHiddenFromTheHeadBehindAPillar()
    {
        var (camera, keys, manager, center) = Setup(HeadStart);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        BuildWall(center, x: 4);

        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);
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
        Tap(camera, keys, manager, Key.F5);
        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);
        Assert.Equal(0f, camera.ShoulderBlend);
        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(2f, 5f, 0f), fpHit);
    }

    [Fact]
    public void OrbitCameraIsCenteredBehindAndAbove()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Tap(camera, keys, manager, Key.F5);
        Tap(camera, keys, manager, Key.F5);
        Assert.Equal(CameraMode.Orbit, camera.Mode);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        var offset = camera.RenderEye - camera.Position;
        Assert.True(offset.X < -2f, $"offset.X {offset.X} should reach behind (+X facing)");
        Assert.True(offset.Y > 0.3f, $"offset.Y {offset.Y} should sit above the head");
        var side = Vector3.Dot(offset, new Vector3(0f, 0f, 1f));
        Assert.True(Math.Abs(side) < 0.05f, $"side {side} should be centered");
    }

    [Fact]
    public void OrbitKeepsPlayerFacingWhileCameraOrbits()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Tap(camera, keys, manager, Key.F5);
        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);
        Assert.Equal(0f, camera.PlayerYaw);

        camera.TestSetCameraLook(60f, 0f);
        Update(camera, keys, manager);

        Assert.Equal(60f, camera.Yaw);
        Assert.Equal(0f, camera.PlayerYaw);
    }

    [Fact]
    public void OrbitMovementTurnsPlayerTowardMotion()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Tap(camera, keys, manager, Key.F5);
        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        camera.TestSetCameraLook(90f, 0f);
        keys.Press([Key.W]);
        for (var i = 0; i < 12; i++)
            Update(camera, keys, manager);
        keys.ReleaseAll();

        Assert.True(camera.PlayerYaw > 80f, $"player yaw {camera.PlayerYaw} should face the motion");
        Assert.True(camera.Position.Z > HeadStart.Z + 1f, "player should walk along the camera forward");
    }

    [Fact]
    public void OrbitClickTurnsPlayerTowardAim()
    {
        var (camera, keys, manager, _) = Setup(HeadStart);

        Tap(camera, keys, manager, Key.F5);
        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        camera.TestSetCameraLook(90f, 0f);
        camera.NotifyPlayerAction();
        for (var i = 0; i < 12; i++)
            Update(camera, keys, manager);

        Assert.True(camera.PlayerYaw > 80f, $"player yaw {camera.PlayerYaw} should face the aim");
    }

    [Fact]
    public void OrbitRaycastHitsTheSameBlockAsFirstPerson()
    {
        var (camera, keys, manager, center) = Setup(HeadStart);
        BuildWall(center, x: 7);

        Assert.True(camera.Raycast(10f, manager, out var fpHit, out _));
        Assert.Equal(new Vector3(7f, 5f, 0f), fpHit);

        Tap(camera, keys, manager, Key.F5);
        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(HeadStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        Assert.True(camera.Raycast(10f, manager, out var orbitHit, out _));
        Assert.Equal(fpHit, orbitHit);
    }

    [Fact]
    public void ShoulderCameraPullsInFrontOfAWallBehindThePlayer()
    {
        var wallStart = new Vector3(8.5f, 5.5f, 8.5f);
        var (camera, keys, manager, center) = Setup(wallStart);
        BuildWall(center, x: 5);

        Tap(camera, keys, manager, Key.F5);
        Settle(camera, keys, manager);
        camera.TestTeleport(wallStart, yaw: 0f, pitch: 0f);
        Update(camera, keys, manager);

        var offsetLength = (camera.RenderEye - camera.Position).Length();
        Assert.True(offsetLength < 3f, $"offset {offsetLength} should be pulled in short of the wall");
        Assert.True(offsetLength > 0.2f, "camera should not collapse all the way into the head");

        var eye = camera.RenderEye;
        Assert.Equal(0, manager.GetBlockGlobal(
            (int)float.Floor(eye.X), (int)float.Floor(eye.Y), (int)float.Floor(eye.Z)));
    }
}
