using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The client's entity reconcile: a server snapshot spawns, updates by network id, removes and
/// rebuilds the mobs and drops the client renders, without the client running any AI. A client
/// world also mirrors the server's primed TNT and falling blocks, display-only: they never move,
/// land or detonate there, and a world that is not a client never takes them.
/// </summary>
[Collection("Worldgen seed")]
public sealed class NetworkEntitySyncTests : IDisposable
{
    private const double Dt = 1.0 / 30.0;

    private World? _world;
    private readonly List<World> _others = [];

    public NetworkEntitySyncTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        foreach (var world in _others)
            world.SaveAndDispose();
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
        Assert.Empty(_world.Mobs);
        Assert.Empty(_world.Drops);
    }

    [Fact]
    public void ASlimesSizeRidesTheSnapshotIntoTheMirror()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;

        // The ammo field carries a slime's size; a full large slime's health is above the small pool
        var pos = new Vector3(4f, 60f, 4f);
        var large = new Slime(EntityRegistry.Require("slime"), pos, SlimeSize.Max);
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, large.MaxHealth, large.Size, 0, Vector3.Zero)]);
        var mirrored = Assert.Single(_world.Slimes);
        Assert.Equal(large.Size, mirrored.Size);
        Assert.Equal(large.MaxHealth, mirrored.Health);
        Assert.Equal(large.BodyHalfExtent, mirrored.BodyHalfExtent);

        // The same id at the same size updates in place
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 2, large.Size, 0, Vector3.Zero)]);
        Assert.Same(mirrored, Assert.Single(_world.Slimes));

        // An older server's 0 reads as the smallest size, which replaces the mirror (a size is for life)
        _world.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 2, 0, 0, Vector3.Zero)]);
        var replaced = Assert.Single(_world.Mobs);
        Assert.NotSame(mirrored, replaced);
        Assert.Equal(SlimeSize.Min, Assert.IsType<Slime>(replaced).Size);
    }

    [Fact]
    public void PrimedTntAndFallingBlocksMirrorFromASnapshot()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;

        var tntId = ContentRegistry.RequireBlock("tnt").Id;
        Assert.Equal(BlockBreakAction.PrimeTnt, BlockType.ById[tntId]!.OnBreak);
        var sand = TerrainGenerator.SandBlock;
        Assert.True(BlockType.IsFalling(sand));

        // The wire carries the fuse in milliseconds: what is left, then the whole fuse
        var tntAt = new Vector3(3.5f, 70.5f, -2.5f);
        var tnt = new NetEntity(NetEntityKind.PrimedTnt, 11, tntAt, 0f, 1500, 2000, tntId, Vector3.Zero);
        var cell = (X: 6, Y: 71, Z: 4);
        var cube = new NetEntity(NetEntityKind.FallingBlock, 12,
            new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f), 0f, 0, 0, sand, Vector3.Zero);
        var slime = new NetEntity(NetEntityKind.Slime, 13, new Vector3(-4f, 60f, 4f), 1f, 3, 0, 0, Vector3.Zero);
        _world.ApplyNetworkEntities([tnt, cube, slime]);

        var mirrored = Assert.Single(_world.PrimedTnt);
        Assert.Equal(tntAt, mirrored.Position);
        Assert.Equal(tntId, mirrored.BlockId);
        Assert.Equal(tnt.Health / 1000f, mirrored.FuseRemaining);
        Assert.Equal(tnt.Ammo / 1000f, mirrored.FuseDuration);

        var falling = Assert.Single(_world.Falling);
        Assert.Equal(sand, falling.BlockId);
        Assert.Equal(cube.Position, falling.Position);
        Assert.True(falling.OverlapsCell(cell.X, cell.Y, cell.Z));
        Assert.False(falling.OverlapsCell(cell.X, cell.Y - 1, cell.Z));

        // Neither kind is taken for a mob: only the slime is mirrored as one
        Assert.Single(_world.Mobs);
        Assert.Single(_world.Slimes);
        Assert.Empty(_world.Drops);

        // The same id updates the same cube in place; a new id is a second cube
        var later = tnt with { Position = tntAt + Vector3.UnitY, Health = tnt.Health / 2 };
        var second = new NetEntity(NetEntityKind.PrimedTnt, 14, tntAt - Vector3.UnitX, 0f, 400, 500, tntId, Vector3.Zero);
        _world.ApplyNetworkEntities([later, second, cube]);
        Assert.Equal(2, _world.PrimedTnt.Count);
        Assert.Contains(_world.PrimedTnt, t => ReferenceEquals(t, mirrored));
        Assert.Equal(later.Position, mirrored.Position);
        Assert.Equal(later.Health / 1000f, mirrored.FuseRemaining);
        Assert.Equal(tnt.Ammo / 1000f, mirrored.FuseDuration);
        Assert.Empty(_world.Mobs);

        // A cube the next snapshot no longer lists goes, while the one still listed stays itself
        _world.ApplyNetworkEntities([later, cube]);
        Assert.Same(mirrored, Assert.Single(_world.PrimedTnt));

        // Client frames burn a mirror's fuse down to nothing and no further, but nothing moves,
        // lands or detonates: the server alone runs both
        var frames = (int)MathF.Ceiling(mirrored.FuseRemaining / (float)Dt) + 5;
        for (var i = 0; i < frames; i++)
            _world.Update(Dt, null);
        Assert.Same(mirrored, Assert.Single(_world.PrimedTnt));
        Assert.Equal(0f, mirrored.FuseRemaining);
        Assert.Equal(later.Position, mirrored.Position);
        Assert.Equal(cube.Position, Assert.Single(_world.Falling).Position);

        // An empty snapshot clears both
        _world.ApplyNetworkEntities([]);
        Assert.Empty(_world.PrimedTnt);
        Assert.Empty(_world.Falling);

        // A world that is not a client never takes mirrors, which its own Update would tick and
        // detonate
        var local = new World(null!, null!, null!, null!, null!, null!, null, null);
        _others.Add(local);
        local.ApplyNetworkEntities([tnt, cube]);
        Assert.Empty(local.PrimedTnt);
        Assert.Empty(local.Falling);
    }
}
