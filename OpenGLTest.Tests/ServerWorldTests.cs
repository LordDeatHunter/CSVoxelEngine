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
}
