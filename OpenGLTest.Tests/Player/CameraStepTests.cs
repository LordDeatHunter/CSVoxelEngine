using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Player;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

public sealed class CameraStepTests
{
    public CameraStepTests() => TestContent.EnsureLoaded();

    private const float Dt = 0.05f;

    // One chunk: a floor at y=0 for x in [0,5) stepping up one block to y=1 from x=5 on.
    private static ChunkManager SteppedFloor(out Camera camera)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, x < 5 ? 0 : 1, z, stone, 0);
        manager.Chunks[Vector3.Zero] = chunk;

        camera = new Camera(null, null);
        // Feet at 1.0 on the lower floor, facing +X into the step at x=5.
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        return manager;
    }

    private static void RunForward(Camera camera, ScriptedKeyboard keys, ChunkManager manager, bool sprint)
    {
        keys.Press(sprint ? [Key.W, Key.ShiftLeft] : [Key.W]);
        for (var i = 0; i < 15; i++)
            camera.Update(Dt, keys, manager);
        keys.ReleaseAll();
    }

    [Fact]
    public void WalkingDoesNotClimbAFullBlockStep()
    {
        var manager = SteppedFloor(out var camera);

        RunForward(camera, new ScriptedKeyboard(), manager, sprint: false);

        Assert.True(camera.Position.X < 5f, $"walked past the step: {camera.Position.X}");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 0.99f, 1.01f);
    }

    [Fact]
    public void SprintingClimbsAFullBlockStep()
    {
        var manager = SteppedFloor(out var camera);

        RunForward(camera, new ScriptedKeyboard(), manager, sprint: true);

        Assert.True(camera.Position.X > 5f, $"did not clear the step: {camera.Position.X}");
        Assert.InRange(camera.Position.Y - Camera.EyeHeight, 1.99f, 2.01f);
    }
}
