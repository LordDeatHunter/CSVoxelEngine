using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Player;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// Lava's drag (<see cref="PlayerConfig.LavaSpeedFactor"/>): a body wading through a shallow lava
/// pool covers far less ground than the same sprint on stone, so a run cannot carry through a dip.
/// </summary>
public sealed class LavaMovementTests
{
    public LavaMovementTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const int Frames = 30;

    private static ChunkManager FloorWithLava(bool lava)
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        if (!lava) return chunks;
        var chunk = chunks.Chunks[Vector3.Zero];
        var id = ContentRegistry.RequireBlock("lava").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 1, z, id, 0);
        return chunks;
    }

    private static Camera SprintAcross(ChunkManager chunks, out float distance)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        var keys = new ScriptedKeyboard();
        keys.Press([Key.W, Key.ShiftLeft]);
        var start = camera.Position.X;
        for (var i = 0; i < Frames; i++)
            camera.Update(Dt, keys, chunks);
        distance = camera.Position.X - start;
        return camera;
    }

    [Fact]
    public void SprintingThroughShallowLavaIsMuchSlowerThanSprintingOnStone()
    {
        var dry = SprintAcross(FloorWithLava(false), out var dryDistance);
        var lava = SprintAcross(FloorWithLava(true), out var lavaDistance);

        Assert.False(dry.IsInLava);
        Assert.True(lava.IsInLava);
        Assert.True(lavaDistance < dryDistance * 0.6f,
            $"lava covered {lavaDistance} against {dryDistance} on stone");
    }
}
