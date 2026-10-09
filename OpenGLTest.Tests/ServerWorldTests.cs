using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A server-mode <see cref="World"/> runs headlessly over real terrain: no geometry pools, no LOD
/// threads, its chunks marked meshed by the headless path. This is the check that the dedicated
/// server can host a saved world without a GL context.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ServerWorldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private World? _world;

    public ServerWorldTests() => TestContent.EnsureLoaded();

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
    }

    [Fact]
    public void HeadlessServerWorldStreamsAndPlaysRealTerrain()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var store = new ChunkSaveStore(_dir);
        _world = new World(null!, null!, null!, null!, null!, null!, null, store, serverMode: true);

        Assert.True(_world.GetChunkManager().Headless);
        Assert.True(WorldSeed.WorldIsLive);

        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (!_world.IsLoaded && sw.Elapsed.TotalSeconds < 45)
        {
            var now = sw.Elapsed.TotalSeconds;
            _world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }

        Assert.True(_world.IsLoaded, "server world never left Loading over real terrain");
    }

    [Fact]
    public void NetworkBlockEditChangesALoadedCell()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var store = new ChunkSaveStore(_dir);
        _world = new World(null!, null!, null!, null!, null!, null!, null, store, serverMode: true);

        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (!_world.IsLoaded && sw.Elapsed.TotalSeconds < 45)
        {
            var now = sw.Elapsed.TotalSeconds;
            _world.Update(Math.Min(now - last, 0.1), null);
            last = now;
            Thread.Sleep(1);
        }

        Assert.True(_world.IsLoaded);
        var manager = _world.GetChunkManager();
        var eye = _world.Camera.Position;
        var x = (int)MathF.Floor(eye.X);
        var y = (int)MathF.Floor(eye.Y);
        var z = (int)MathF.Floor(eye.Z);
        // The eye sits in air above the surface, so the cell is free to write.
        Assert.Equal(0, manager.GetBlockGlobal(x, y, z));

        Assert.True(_world.ApplyNetworkBlockEdit(x, y, z, TerrainGenerator.RockBlock, 0));
        Assert.Equal(TerrainGenerator.RockBlock, manager.GetBlockGlobal(x, y, z));

        // An occupied cell is replaced outright (a converted structure, a correction)
        Assert.True(_world.ApplyNetworkBlockEdit(x, y, z, TerrainGenerator.SoilBlock, 0));
        Assert.Equal(TerrainGenerator.SoilBlock, manager.GetBlockGlobal(x, y, z));

        Assert.True(_world.ApplyNetworkBlockEdit(x, y, z, 0, 0));
        Assert.Equal(0, manager.GetBlockGlobal(x, y, z));

        // Removing what is already gone changed nothing, so the server corrects the sender
        Assert.False(_world.ApplyNetworkBlockEdit(x, y, z, 0, 0));

        // A poured fluid is taken out again by an id-0 edit: a client's bucket scoop
        var water = ContentRegistry.RequireBlock("water").Id;
        Assert.True(_world.ApplyNetworkBlockEdit(x, y, z, water, FluidState.Source));
        Assert.Equal(water, manager.GetBlockGlobal(x, y, z));
        Assert.True(_world.ApplyNetworkBlockEdit(x, y, z, 0, 0));
        Assert.Equal(0, manager.GetBlockGlobal(x, y, z));

        // A cell whose chunk is not loaded is never applied
        Assert.False(_world.ApplyNetworkBlockEdit(x + 1_000_000, y, z, TerrainGenerator.RockBlock, 0));
    }

    [Fact]
    public void HostingWritesTheSavedPlayerBackNotTheServerCamera()
    {
        // A survival player saved standing somewhere: hosting forces the server camera creative,
        // flying and onto the players' centroid, none of which may reach player.json
        Directory.CreateDirectory(_dir);
        var saved = new PlayerSaveData
        {
            Position = [12.5f, 90f, -3.5f],
            Yaw = 45f,
            Pitch = -10f,
            Creative = false,
            Flying = false,
            Health = 70,
            RespawnEye = [1f, 80f, 2f],
        };
        File.WriteAllBytes(Path.Combine(_dir, PlayerSaveCodec.FileName), PlayerSaveCodec.ToBytes(saved));

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir), serverMode: true);
        Assert.True(_world.Camera.IsCreative && _world.Camera.IsFlying);
        _world.SetServerFocus(new Vector3(500f, 120f, 500f));
        _world.SaveAndDispose();

        var written = PlayerSaveCodec.TryParse(File.ReadAllBytes(Path.Combine(_dir, PlayerSaveCodec.FileName)));
        Assert.NotNull(written);
        Assert.Equal(saved.Position, written!.Position);
        Assert.Equal(saved.Yaw, written.Yaw);
        Assert.Equal(saved.Pitch, written.Pitch);
        Assert.False(written.Creative);
        Assert.False(written.Flying);
        Assert.Equal(saved.Health, written.Health);
        Assert.Equal(saved.RespawnEye, written.RespawnEye);
        Assert.NotNull(written.LightingClock);
        Assert.NotNull(written.Inventory);
    }

    [Fact]
    public void HostingANewWorldSavesNoPlayerPose()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir), serverMode: true);
        _world.SetServerFocus(new Vector3(500f, 120f, 500f));
        _world.SaveAndDispose();

        var written = PlayerSaveCodec.TryParse(File.ReadAllBytes(Path.Combine(_dir, PlayerSaveCodec.FileName)));
        Assert.NotNull(written);
        // Opened later in singleplayer, the world searches the surface for a survival spawn
        Assert.Null(written!.Position);
        Assert.False(written.Creative);
        Assert.False(written.Flying);
        Assert.Null(written.RespawnEye);
    }
}
