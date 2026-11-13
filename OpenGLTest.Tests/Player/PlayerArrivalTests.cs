using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Player;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The arrival after a teleport (respawn, <c>/tp</c>, waystone): the player is held over a destination whose cells are
/// not loaded, since physics reads them as air, and once they are in it lands out of any solid by the body's own
/// collision rule.
/// </summary>
public sealed class PlayerArrivalTests
{
    public PlayerArrivalTests() => TestContent.EnsureLoaded();

    private const float Dt = 0.05f;

    /// <summary>One meshed chunk at the origin with a stone floor at y = 0.</summary>
    private static ChunkManager FlatFloor(out Chunk chunk)
    {
        var manager = new ChunkManager(null!);
        chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.UpdateMesh(new ChunkMeshData([], [], [], [], false), null, null);
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    private static void Frames(PlayerArrival arrival, Camera camera, ChunkManager manager, int count)
    {
        var keys = new ScriptedKeyboard();
        for (var i = 0; i < count; i++)
        {
            arrival.Update(manager, Dt);
            camera.Update(Dt, keys, manager);
        }
    }

    private static float Feet(Camera camera) => camera.Position.Y - Camera.EyeHeight;

    [Fact]
    public void DestinationOverUnloadedCellsHoldsThePlayerInsteadOfLettingItFall()
    {
        var manager = FlatFloor(out _);
        var camera = new Camera(null, null);
        var arrival = new PlayerArrival(camera, _ => { });
        var destination = new Vector3(8.5f + 4 * Chunk.Size, 3f, 8.5f);

        arrival.BeginAt(destination);
        Frames(arrival, camera, manager, 40);

        Assert.True(arrival.IsArriving);
        Assert.InRange(Feet(camera), destination.Y - 0.1f, destination.Y + 0.1f);
    }

    [Fact]
    public void DestinationInsideASolidLandsOnTopOfIt()
    {
        var manager = FlatFloor(out var chunk);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y <= 3; y++)
            chunk.SetBlockRaw(8, y, 8, stone, 0);
        var camera = new Camera(null, null);
        var arrival = new PlayerArrival(camera, _ => { });

        arrival.BeginAt(new Vector3(8.5f, 1f, 8.5f));
        Frames(arrival, camera, manager, 20);

        Assert.False(arrival.IsArriving);
        Assert.False(BodyCollision.BodyCollides(camera.Position, manager));
        Assert.True(camera.IsGrounded, "did not land on the pillar");
        Assert.InRange(Feet(camera), 3.99f, 4.06f);
    }

    [Fact]
    public void AFreeDestinationIsKeptAsItIs()
    {
        var manager = FlatFloor(out _);
        var camera = new Camera(null, null);
        var arrival = new PlayerArrival(camera, _ => { });

        arrival.BeginAt(new Vector3(8.5f, 1f, 8.5f));
        Frames(arrival, camera, manager, 20);

        Assert.False(arrival.IsArriving);
        Assert.InRange(Feet(camera), 0.99f, 1.06f);
    }

    [Fact]
    public void SomebodyElsesTeleportWhileHeldCancelsTheArrival()
    {
        var manager = FlatFloor(out _);
        var camera = new Camera(null, null);
        var arrival = new PlayerArrival(camera, _ => { });

        arrival.BeginAt(new Vector3(8.5f + 4 * Chunk.Size, 3f, 8.5f));
        camera.TestTeleport(new Vector3(8.5f, 1f + Camera.EyeHeight, 8.5f), 0f, 0f);
        arrival.Update(manager, Dt);

        Assert.False(arrival.IsArriving);
    }
}
