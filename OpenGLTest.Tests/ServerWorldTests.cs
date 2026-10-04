using System;
using System.Diagnostics;
using System.IO;
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

        _world.ApplyNetworkBlockEdit(x, y, z, TerrainGenerator.RockBlock, 0);
        Assert.Equal(TerrainGenerator.RockBlock, manager.GetBlockGlobal(x, y, z));

        _world.ApplyNetworkBlockEdit(x, y, z, 0, 0);
        Assert.Equal(0, manager.GetBlockGlobal(x, y, z));
    }
}
