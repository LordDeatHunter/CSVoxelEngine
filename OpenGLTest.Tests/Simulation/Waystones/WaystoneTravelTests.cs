using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>
/// A singleplayer waystone trip in a real (headless) world: from one discovered waystone to another whose chunks are
/// not even loaded, the traveler is held over the destination until it streams in and then stands beside it on the
/// ground; the trip drained the mana, so the way back is refused until the pool is over half again, while a creative
/// traveler goes free.
/// </summary>
[Collection("Worldgen seed")]
public sealed class WaystoneTravelTests : IDisposable
{
    private const double Dt = 1.0 / 30.0;
    private const int FloorY = 10;
    private const int Reach = 48;

    private static readonly (int X, int Y, int Z) Home = (0, FloorY + 1, 0);
    private static readonly (int X, int Y, int Z) Far = (400, FloorY + 1, 0);

    private World? _world;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public WaystoneTravelTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
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

    private static Vector3 EyeBeside((int X, int Y, int Z) waystone) =>
        new(waystone.X + 0.5f, waystone.Y + Camera.EyeHeight + 0.02f, waystone.Z + 2.5f);

    private static Vector3 Cell((int X, int Y, int Z) c) => new(c.X, c.Y, c.Z);

    private World FlatWorld()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var source = new FlatWorldSource(-Reach, Reach, -Reach, Reach, FloorY, ContentRegistry.RequireBlock("stone").Id,
            Biome.Forest, warmth: 0.5f, wetness: 0.5f);
        // Store-backed, so the far waystone survives its chunk unloading while the player is away
        var world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir),
            spawn: EyeBeside(Home), worldSource: source);
        world.GetChunkManager().Headless = true;
        _world = world;
        RunUntil(world, () => world.IsPlaying, "the world to load");
        return world;
    }

    private static void RunUntil(World world, Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed.TotalSeconds < 60)
        {
            world.Update(Dt, null);
            Thread.Sleep(1);
        }
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static bool Loaded(World world, (int X, int Y, int Z) c) =>
        world.GetChunkManager().TryGetCell(c.X, c.Y, c.Z, out _, out _);

    /// <summary>Stands the camera beside a waystone cell, waits for it to load, builds the waystone and discovers it.</summary>
    private static void BuildAndDiscover(World world, (int X, int Y, int Z) waystone)
    {
        world.Camera.TestTeleport(EyeBeside(waystone), 0f, 0f);
        RunUntil(world, () => Loaded(world, waystone), $"the chunk of {waystone}");
        var chunks = world.GetChunkManager();
        var chunk = chunks.ChunkAt(waystone.X, waystone.Y, waystone.Z)!;
        var (lx, ly, lz) = VoxelCoords.LocalOf(waystone.X, waystone.Y, waystone.Z);
        chunk.SetBlockRaw(lx, ly, lz, ContentRegistry.RequireBlock("waystone_surface").Id, 0);
        chunk.SetBlockRaw(lx, ly + 1, lz, ContentRegistry.RequireBlock("waystone_surface_upper").Id, 0);
        Assert.NotNull(world.DiscoverWaystoneFor(world.LocalPlayer.LocalContext(), Cell(waystone)));
    }

    [Fact]
    public void ATripToAnUnloadedWaystoneHoldsTheTravelerUntilItLoadsThenLandsBesideIt()
    {
        var world = FlatWorld();
        BuildAndDiscover(world, Far);
        BuildAndDiscover(world, Home);
        RunUntil(world, () => !Loaded(world, Far), "the far waystone's chunk to unload");

        Assert.Equal(WaystoneTeleportVerdict.Teleport, world.TeleportViaWaystone(Cell(Home), Cell(Far)));
        Assert.Equal(0f, world.Mana.Current);
        Assert.True(world.WaystoneTravel.IsArriving);

        RunUntil(world, () => !world.WaystoneTravel.IsArriving, "the arrival");
        for (var i = 0; i < 30; i++)
            world.Update(Dt, null);

        var feet = world.Camera.Position - new Vector3(0f, Camera.EyeHeight, 0f);
        Assert.InRange(feet.Y, Far.Y - 0.01f, Far.Y + 0.1f);
        var away = new Vector2(feet.X - (Far.X + 0.5f), feet.Z - (Far.Z + 0.5f)).Length();
        Assert.InRange(away, 0.5f, WaystoneArrival.SearchRadius * 1.5f);
        Assert.False(world.LocalPlayer.RespawnPrompt.IsShowing);
        Assert.Equal(PlayerHealth.MaxHealth, world.Health);

        // Drained, the way back is refused and changes nothing; a creative traveler goes free
        var before = world.Camera.Position;
        Assert.Equal(WaystoneTeleportVerdict.NeedsMana, world.TeleportViaWaystone(Cell(Far), Cell(Home)));
        Assert.False(world.WaystoneTravel.IsArriving);
        Assert.Equal(before, world.Camera.Position);

        world.Camera.SetModes(creative: true, flying: false);
        Assert.Equal(WaystoneTeleportVerdict.Teleport, world.TeleportViaWaystone(Cell(Far), Cell(Home)));
        Assert.True(world.WaystoneTravel.IsArriving);
    }

    [Fact]
    public void ATripNeedsThePlayerToStandAtTheWaystoneItLeavesFrom()
    {
        var world = FlatWorld();
        BuildAndDiscover(world, Home);
        var other = (Home.X + 6, Home.Y, Home.Z);
        BuildAndDiscover(world, other);

        world.Camera.TestTeleport(EyeBeside(Home) + new Vector3(60f, 0f, 0f), 0f, 0f);
        Assert.Equal(WaystoneTeleportVerdict.NotAtWaystone, world.TeleportViaWaystone(Cell(Home), Cell(other)));
        Assert.True(world.Mana.MoreThanHalf);
    }
}
