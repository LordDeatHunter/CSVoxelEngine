using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The client's entity reconcile: a server snapshot spawns, updates by network id, removes and
/// rebuilds the mobs and drops the client renders, without the client running any AI.
/// </summary>
[Collection("Worldgen seed")]
public sealed class NetworkEntitySyncTests : IDisposable
{
    private World? _world;

    public NetworkEntitySyncTests() => TestContent.EnsureLoaded();

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
    }

    [Fact]
    public void MobsAndDropsReconcileFromASnapshot()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);

        var pos = new Vector3(4f, 60f, 4f);
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 3, 0, 0, Vector3.Zero)]);
        Assert.Single(_world.Slimes);
        Assert.Equal(pos, _world.Slimes[0].Position);
        Assert.Equal(3, _world.Slimes[0].Health);

        // The same network id updates in place rather than spawning a second mob.
        var moved = pos + new Vector3(2f, 0f, 0f);
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, moved, 1f, 2, 0, 0, Vector3.Zero)]);
        Assert.Single(_world.Slimes);
        Assert.Equal(moved, _world.Slimes[0].Position);
        Assert.Equal(2, _world.Slimes[0].Health);

        var stone = TerrainGenerator.RockBlock;
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Drop, 9, pos, 0f, 0, 0, stone, Vector3.Zero)]);
        Assert.Single(_world.Drops);
        Assert.Equal(stone, _world.Drops[0].BlockId);

        // An empty snapshot removes everything.
        _world.ApplyNetworkEntities([]);
        Assert.Empty(_world.Slimes);
        Assert.Empty(_world.Drops);
    }
}
