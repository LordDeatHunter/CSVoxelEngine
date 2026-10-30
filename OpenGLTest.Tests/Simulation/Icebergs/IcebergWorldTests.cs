using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.VisualTest;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Icebergs;

/// <summary>
/// Icebergs in a real (headless) <see cref="World"/> over a flat ocean: the scenario's spawn op
/// floats one at the waterline and the player can stand on it, a block cannot be placed inside a
/// berg but can above its low ledge, and a saved berg is restored once its whole footprint has
/// loaded.
/// </summary>
[Collection("Worldgen seed")]
public sealed class IcebergWorldTests : IDisposable
{
    private const double Dt = 1.0 / 30.0;
    // The supplied ocean's reach in chunk columns, so a test can send the player far away
    private const int OceanReach = 256;

    private readonly List<World> _worlds = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public IcebergWorldTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        foreach (var world in _worlds)
            world.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private World OceanWorld(Vector3 eye, ChunkSaveStore? store = null, bool serverMode = false)
    {
        WorldSeed.Apply(WorldSeed.Default);
        var source = new FlatWorldSource(-OceanReach, OceanReach, -OceanReach, OceanReach, IcebergTestWorld.FloorY,
            ContentRegistry.RequireBlock("stone").Id, Biome.Ocean, warmth: 0f, wetness: 0.5f,
            waterLevel: WorldGenerationConfig.SeaLevel);
        var world = new World(null!, null!, null!, null!, null!, null!, null, store, serverMode: serverMode,
            spawn: eye, worldSource: source);
        world.GetChunkManager().Headless = true;
        _worlds.Add(world);
        return world;
    }

    /// <summary>Streams in the two chunk layers around the waterline over the columns around (x, z).</summary>
    private static void LoadSea(World world, float x, float z, int radius = 1)
    {
        var chunks = world.GetChunkManager();
        var center = VoxelCoords.ChunkPosOf(new Vector3(x, 0f, z));
        var wanted = new List<Vector3>();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        for (var cy = -1; cy <= 0; cy++)
            wanted.Add(new Vector3(center.X + dx, cy, center.Z + dz));
        foreach (var pos in wanted)
            chunks.QueueChunk(pos);

        var sw = Stopwatch.StartNew();
        while (!wanted.All(p => chunks.Chunks.TryGetValue(p, out var c) && c.IsBlockDataReady)
               && sw.Elapsed.TotalSeconds < 30)
            Thread.Sleep(1);
        Assert.True(wanted.All(p => chunks.Chunks.ContainsKey(p)), $"the sea around ({x}, {z}) never loaded");
    }

    [Fact]
    public void TheSpawnOpPlacesABergAndThePlayerCanStandOnIt()
    {
        var world = OceanWorld(new Vector3(20.5f, 10f, 0.5f));
        LoadSea(world, 0.5f, 0.5f);

        Assert.True(world.SpawnEntity("iceberg", new Vector3(0.5f, 1f, 0.5f)));
        var berg = Assert.Single(world.Icebergs);
        var top = berg.Boxes.OrderByDescending(b => b.Max.Y).First();
        Assert.True(berg.Center.Y > WorldGenerationConfig.SeaLevel, "the berg does not float at the waterline");

        // Dropped onto the top step, the player lands and stays on it
        var camera = world.Camera;
        camera.TestTeleport(StubTarget.EyeAt(new Vector3((top.Min.X + top.Max.X) * 0.5f, top.Max.Y + 1f,
            (top.Min.Z + top.Max.Z) * 0.5f)), 0f, 0f);
        var keys = new ScriptedKeyboard();
        for (var i = 0; i < 90; i++)
            world.Update(Dt, keys);

        Assert.True(camera.IsGrounded);
        var feet = camera.Position.Y - Camera.EyeHeight;
        top = berg.Boxes.OrderByDescending(b => b.Max.Y).First();
        Assert.InRange(feet, top.Max.Y - 1e-3f, top.Max.Y + BoxSweep.StepSize);
    }

    [Fact]
    public void APlacementInsideABergIsRefusedButAboveALowLedgeIsAllowed()
    {
        var eye = new Vector3(20.5f, 10f, 0.5f);
        var world = OceanWorld(eye, serverMode: true);
        var player = new ServerPlayer(1, "Builder", eye);
        world.ServerPlayers = [player];
        LoadSea(world, 0.5f, 0.5f);
        Assert.True(world.SpawnEntity("iceberg", new Vector3(0.5f, 1f, 0.5f)));
        var berg = Assert.Single(world.Icebergs);
        var chunks = world.GetChunkManager();
        var stone = ContentRegistry.RequireBlock("stone").Id;

        // A cell the shelf reaches into, and the cell over the shelf's rim, above its top
        var shelf = berg.Boxes.First(b => b.Min.Y < berg.Center.Y && b.Max.Y > berg.Center.Y);
        var rimX = MathUtils.FloorToInt(shelf.Max.X - 0.5f);
        var z = MathUtils.FloorToInt(berg.Center.Z);
        var inside = (X: rimX, Y: MathUtils.FloorToInt(shelf.Max.Y), Z: z);
        var above = (X: rimX, Y: MathUtils.FloorToInt(shelf.Max.Y) + 1, Z: z);
        Assert.True(world.IcebergManager!.OverlapsCell(inside.X, inside.Y, inside.Z));
        Assert.False(world.IcebergManager.OverlapsCell(above.X, above.Y, above.Z));

        // Each is placed against a stone block written just east of it
        foreach (var cell in new[] { inside, above })
        {
            var hit = new Vector3(cell.X + 1, cell.Y, cell.Z);
            var chunk = chunks.ChunkAt((int)hit.X, (int)hit.Y, (int)hit.Z)!;
            var (lx, ly, lz) = VoxelCoords.LocalOf((int)hit.X, (int)hit.Y, (int)hit.Z);
            chunk.SetBlockRaw(lx, ly, lz, stone, 0);
        }

        player.Inventory.Add(stone, 2);
        Assert.True(player.Inventory.PickBlock(stone));
        Assert.False(world.TryPlaceSelectedBlockAt(player.Context, new Vector3(inside.X + 1, inside.Y, inside.Z),
            new Vector3(-1f, 0f, 0f)));
        Assert.NotEqual(stone, chunks.GetBlockGlobal(inside.X, inside.Y, inside.Z));
        Assert.True(world.TryPlaceSelectedBlockAt(player.Context, new Vector3(above.X + 1, above.Y, above.Z),
            new Vector3(-1f, 0f, 0f)));
        Assert.Equal(stone, chunks.GetBlockGlobal(above.X, above.Y, above.Z));
    }

    [Fact]
    public void ASavedBergIsRestoredOnceItsWholeFootprintLoads()
    {
        // A berg saved far out at sea, beyond what streams around the player at the start
        const float far = 2000.5f;
        var center = new Vector3(far, WorldGenerationConfig.SeaLevel + 0.5f, 0.5f);
        var saved = new EntitySaveData
        {
            Icebergs =
            [
                new EntitySaveData.IcebergData
                {
                    Variant = 1, Scale = IcebergConfig.ScaleMax, Turns = 1, Seed = 42,
                    Position = [center.X, center.Y, center.Z], Velocity = [0f, 0f, 0f], Life = 100f,
                },
            ],
        };
        var writer = new ChunkSaveStore(_dir);
        writer.EnqueueFile(EntitySaveCodec.FileName, EntitySaveCodec.ToBytes(saved));
        Assert.True(writer.Shutdown(TimeSpan.FromSeconds(10)));

        var world = OceanWorld(new Vector3(0.5f, 10f, 0.5f), new ChunkSaveStore(_dir));
        world.Camera.TestSetFlight(true);
        var sw = Stopwatch.StartNew();
        while (!world.IsPlaying && sw.Elapsed.TotalSeconds < 60)
        {
            world.Update(0.05, null);
            Thread.Sleep(1);
        }
        Assert.True(world.IsPlaying);
        for (var i = 0; i < 20; i++)
            world.Update(Dt, null);
        Assert.Empty(world.Icebergs);

        // Flown out to it, the berg appears once every chunk under it is in
        world.Camera.TestTeleport(new Vector3(far, 10f, 0.5f), 0f, 0f);
        sw.Restart();
        while (world.Icebergs.Count == 0 && sw.Elapsed.TotalSeconds < 60)
        {
            world.Update(Dt, null);
            Thread.Sleep(1);
        }

        var berg = Assert.Single(world.Icebergs);
        Assert.Equal(1, berg.Variant);
        Assert.Equal(1, berg.Turns);
        Assert.Equal(IcebergConfig.ScaleMax, berg.Scale);
        Assert.Equal(42, berg.Seed);
        var (min, max) = berg.Bounds;
        Assert.True(VoxelEngine.Simulation.Icebergs.IcebergManager.ChunksLoaded(world.GetChunkManager(), min, max));
    }
}
