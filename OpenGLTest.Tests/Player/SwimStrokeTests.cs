using System;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Player;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

public sealed class SwimStrokeTests
{
    public SwimStrokeTests() => TestContent.EnsureLoaded();

    private const float Dt = 0.02f;

    // A deep pool: stone floor at y = 0 and still water from y = 1 up to y = 12 over the whole chunk.
    private static ChunkManager Pool(out Camera camera)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            chunk.SetBlockRaw(x, 0, z, stone, 0);
            for (var y = 1; y <= 12 && y < Chunk.Size; y++)
                chunk.SetBlockRaw(x, y, z, water, 0);
        }
        manager.Chunks[Vector3.Zero] = chunk;

        camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(3.5f, 8f, 8.5f), yaw: 0f, pitch: 0f);
        return manager;
    }

    // Horizontal speed over one frame.
    private static float Step(Camera camera, ChunkManager manager, ScriptedKeyboard keys)
    {
        var before = camera.Position;
        camera.Update(Dt, keys, manager);
        var after = camera.Position;
        return new Vector2(after.X - before.X, after.Z - before.Z).Length() / Dt;
    }

    [Fact]
    public void ASwimmerIsKickedByAStrokeAndThenGlidesWithoutPaddling()
    {
        var manager = Pool(out var camera);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);
        Step(camera, manager, keys);
        Assert.True(camera.IsInWater);
        Assert.Equal(1, camera.SwimStrokeCount);

        // Hold forward for most of a stroke: the speed only ever falls (a glide), it never builds up again.
        var speeds = new System.Collections.Generic.List<float>();
        for (var i = 0; i < (int)(PlayerPhysics.SwimStrokeSeconds * 0.8f / Dt); i++)
            speeds.Add(Step(camera, manager, keys));

        Assert.Equal(1, camera.SwimStrokeCount);
        Assert.True(speeds[0] > PlayerPhysics.SwimBurstSpeed * 0.7f);
        for (var i = 1; i < speeds.Count; i++)
            Assert.True(speeds[i] <= speeds[i - 1] + 0.01f, $"speed rose at frame {i}");
        Assert.True(speeds[^1] < speeds[0] * 0.5f);
    }

    [Fact]
    public void TheNextStrokeIsReadyOnlyAfterTheStrokeTime()
    {
        var manager = Pool(out var camera);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W]);

        var frames = (int)MathF.Ceiling(PlayerPhysics.SwimStrokeSeconds / Dt);
        for (var i = 0; i < frames - 3; i++)
            Step(camera, manager, keys);
        Assert.Equal(1, camera.SwimStrokeCount);

        for (var i = 0; i < 8; i++)
            Step(camera, manager, keys);
        Assert.Equal(2, camera.SwimStrokeCount);
    }

    [Fact]
    public void NoInputMeansNoStrokeAndTheSwimmerDrifts()
    {
        var manager = Pool(out var camera);
        var keys = new ScriptedKeyboard();
        for (var i = 0; i < 100; i++)
            Step(camera, manager, keys);

        Assert.Equal(0, camera.SwimStrokeCount);
    }

    [Fact]
    public void SpaceStrokesUpwardAndCtrlStrokesDown()
    {
        var manager = Pool(out var up);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.Space]);
        var startY = up.Position.Y;
        for (var i = 0; i < 20; i++)
            up.Update(Dt, keys, manager);
        Assert.True(up.Position.Y > startY + 0.5f);
        Assert.Equal(1, up.SwimStrokeCount);

        var manager2 = Pool(out var down);
        var keys2 = new ScriptedKeyboard();
        keys2.Press([Key.ControlLeft]);
        var startDown = down.Position.Y;
        for (var i = 0; i < 20; i++)
            down.Update(Dt, keys2, manager2);
        Assert.True(down.Position.Y < startDown - 0.5f);
    }
}
