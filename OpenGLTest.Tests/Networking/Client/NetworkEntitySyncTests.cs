using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Networking;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Networking.Client;

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
    public void HellMobsMirrorByKindFromASnapshotAndPlayTheirDeathWhenDropped()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);

        var brutePos = new Vector3(4f, 60f, 4f);
        var casterPos = new Vector3(-4f, 60f, 4f);
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Hellbrute, 7, brutePos, 1f, 30, 0, 0, Vector3.Zero),
            new NetEntity(NetEntityKind.Cinderwitch, 8, casterPos, 0.5f, 12, 0, 0, Vector3.Zero),
        ]);
        Assert.Equal(2, _world.HellMobs.Count);
        var brute = Assert.IsType<Hellbrute>(Assert.Single(_world.HellMobs, m => m is Hellbrute));
        var caster = Assert.IsType<Cinderwitch>(Assert.Single(_world.HellMobs, m => m is Cinderwitch));
        Assert.Equal(brutePos, brute.Position);
        Assert.Equal(30, brute.Health);
        Assert.Equal(12, caster.Health);

        // The same id updates in place; a kind that changed under one id is replaced, not reused
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Hellbrute, 7, brutePos + Vector3.UnitX, 1f, 20, 0, 0, Vector3.Zero),
            new NetEntity(NetEntityKind.Hellbrute, 8, casterPos, 0.5f, 12, 0, 0, Vector3.Zero),
        ]);
        Assert.Same(brute, Assert.Single(_world.HellMobs, m => m.Position == brutePos + Vector3.UnitX));
        Assert.Equal(20, brute.Health);
        Assert.True(caster.IsDying);

        // A mob the server stops reporting plays its death and is then dropped
        _world.ClientMirror.ApplyNetworkEntities([]);
        Assert.True(brute.IsDying);
        for (var i = 0; i < 120 && _world.HellMobs.Count > 0; i++)
            _world.Update(Dt, null);
        Assert.Empty(_world.HellMobs);
    }

    [Fact]
    public void ASkeletonMirrorTakesItsTierFromTheSnapshotAndIsReplacedWhenTheTierDiffers()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);

        var pos = new Vector3(4f, 60f, 4f);
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Skeleton, 7, pos, 1f, 25, 0, 4, Vector3.Zero),
        ]);
        var skeleton = Assert.IsType<Skeleton>(Assert.Single(_world.Skeletons));
        // Nothing about where it stands names its floor: the tier is the snapshot's, and so is its scaled pool
        Assert.Equal(4, skeleton.Tier);
        Assert.True(skeleton.MaxHealth > EntityRegistry.Require(Skeleton.EntityName).MaxHealth);
        Assert.Equal(25, skeleton.Health);

        // The same tier updates in place, and its attack count plays the clip the count names
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Skeleton, 7, pos + Vector3.UnitX, 1f, 20, 1, 4, Vector3.Zero),
        ]);
        Assert.Same(skeleton, Assert.Single(_world.Skeletons, s => !s.IsDying));
        Assert.Equal(20, skeleton.Health);
        Assert.True(skeleton.Animation.IsPlaying(Skeleton.ShootClip));

        // A different tier under the same id is a different mob: the old one dies, a new one takes the tier
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Skeleton, 7, pos, 1f, 20, 1, 6, Vector3.Zero),
        ]);
        Assert.True(skeleton.IsDying);
        var replaced = Assert.Single(_world.Skeletons, s => !s.IsDying);
        Assert.NotSame(skeleton, replaced);
        Assert.Equal(6, replaced.Tier);

        // A tier past the tower's top floor clamps, and then matches the clamped mirror instead of churning
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Skeleton, 8, pos, 1f, 20, 0, 255, Vector3.Zero),
        ]);
        var clamped = Assert.Single(_world.Skeletons, s => !s.IsDying && s != replaced);
        Assert.Equal(MobConfig.TowerMaxTier, clamped.Tier);
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Skeleton, 8, pos, 1f, 20, 0, 255, Vector3.Zero),
        ]);
        Assert.False(clamped.IsDying);
    }

    [Fact]
    public void SkyMobsMirrorByKindAndAFlyerKeepsItsHeight()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);

        var raptorPos = new Vector3(4f, WorldGenerationConfig.SkyIslandBottomY + 40f, 4f);
        var golemPos = new Vector3(-4f, WorldGenerationConfig.SkyIslandBottomY + 20f, 4f);
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Galeraptor, 7, raptorPos, 1f, 20, 0, 0, Vector3.Zero),
            new NetEntity(NetEntityKind.Cloudgolem, 8, golemPos, 0.5f, 90, 0, 0, Vector3.Zero),
        ]);
        Assert.Equal(2, _world.SkyMobs.Count);
        var raptor = Assert.IsType<Galeraptor>(Assert.Single(_world.SkyMobs, m => m is Galeraptor));
        var golem = Assert.IsType<Cloudgolem>(Assert.Single(_world.SkyMobs, m => m is Cloudgolem));
        Assert.Equal(raptorPos, raptor.Position);
        Assert.Equal(golemPos, golem.Position);
        Assert.Equal(90, golem.Health);

        // A mirrored flyer neither falls nor drifts between snapshots, and the next one moves it in 3D
        for (var i = 0; i < 60; i++)
            raptor.AnimateMirror((float)Dt);
        Assert.Equal(raptorPos.Y, raptor.Position.Y);
        var climbed = raptorPos + new Vector3(1f, 3f, 0f);
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Galeraptor, 7, climbed, 1f, 20, 0, 0, new Vector3(0f, 2f, 0f)),
            new NetEntity(NetEntityKind.Cloudgolem, 8, golemPos, 0.5f, 90, 0, 0, Vector3.Zero),
        ]);
        Assert.Same(raptor, Assert.Single(_world.SkyMobs, m => m is Galeraptor));
        Assert.Equal(climbed, raptor.Position);

        // A kind that changed under one id is replaced, and a mob the server drops plays its death
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Cloudgolem, 7, climbed, 1f, 20, 0, 0, Vector3.Zero),
        ]);
        Assert.True(raptor.IsDying);
        Assert.True(golem.IsDying);
    }

    [Fact]
    public void ASandwormMirrorsItsPhaseAndTargetAndDragsItsChainAfterTheHead()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        var head = new Vector3(4f, 40f, 4f);
        var target = new Vector3(20f, 66f, 4f);
        var wounded = EntityRegistry.Require(Sandworm.EntityName).MaxHealth - 10;

        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Sandworm, 5, head, 0.5f, wounded, (int)SandwormPhase.Dig, 0, Vector3.Zero),
            new NetEntity(NetEntityKind.SandwormTarget, 5, target, 0f, 750, 0, 0, Vector3.Zero),
        ]);

        var worm = Assert.Single(_world.Sandworms);
        Assert.True(worm.IsMirror);
        Assert.Equal(SandwormPhase.Dig, worm.Phase);
        Assert.Equal(head, worm.Position);
        Assert.Equal(wounded, worm.Health);
        Assert.Equal(target, worm.SurfacePoint);
        Assert.Equal(0.75f, worm.Telegraph, 3);
        Assert.Contains(worm, _world.Mobs);

        // Between snapshots the head dead-reckons along the server's velocity and the body is dragged after it
        var velocity = new Vector3(8f, 0f, 0f);
        _world.ClientMirror.ApplyNetworkEntities([
            new NetEntity(NetEntityKind.Sandworm, 5, head, 0.5f, wounded, (int)SandwormPhase.Dig, 0, velocity),
        ]);
        var tailBefore = worm.Chain[worm.Chain.Count - 1];
        for (var i = 0; i < 120; i++)
            worm.AnimateMirrorFrame((float)Dt, (_, _) => (65f, true));
        Assert.True(worm.Position.X > head.X + 10f);
        Assert.True(worm.Chain[worm.Chain.Count - 1].X > tailBefore.X);
        // No target entry this snapshot: nothing to telegraph
        Assert.Equal(0f, worm.Telegraph);

        // A phase change restarts the phase clock; the server dropping the worm plays its death
        worm.MirrorPhase(SandwormPhase.Breach);
        Assert.Equal(SandwormPhase.Breach, worm.Phase);
        Assert.Equal(0f, worm.PhaseSeconds);
        _world.ClientMirror.ApplyNetworkEntities([]);
        Assert.True(worm.IsDying);
    }

    [Fact]
    public void TheSandwormHeadIsAMobEntryAndItsTargetIsNot()
    {
        Assert.True(NetEntity.IsMob(NetEntityKind.Sandworm));
        Assert.False(NetEntity.IsMob(NetEntityKind.SandwormTarget));
    }

    [Fact]
    public void MobsAndDropsReconcileFromASnapshot()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);

        var pos = new Vector3(4f, 60f, 4f);
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 3, 0, 0, Vector3.Zero)]);
        Assert.Single(_world.Slimes);
        Assert.Equal(pos, _world.Slimes[0].Position);
        Assert.Equal(3, _world.Slimes[0].Health);

        // The same network id updates in place rather than spawning a second mob.
        var moved = pos + new Vector3(2f, 0f, 0f);
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, moved, 1f, 2, 0, 0, Vector3.Zero)]);
        Assert.Single(_world.Slimes);
        Assert.Equal(moved, _world.Slimes[0].Position);
        Assert.Equal(2, _world.Slimes[0].Health);

        var stone = TerrainGenerator.RockBlock;
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Drop, 9, pos, 0f, 0, 0, stone, Vector3.Zero)]);
        Assert.Single(_world.Drops);
        Assert.Equal(stone, _world.Drops[0].BlockId);

        // An empty snapshot removes everything.
        _world.ClientMirror.ApplyNetworkEntities([]);
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
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, large.MaxHealth, large.Size, 0, Vector3.Zero)]);
        var mirrored = Assert.Single(_world.Slimes);
        Assert.Equal(large.Size, mirrored.Size);
        Assert.Equal(large.MaxHealth, mirrored.Health);
        Assert.Equal(large.BodyHalfExtent, mirrored.BodyHalfExtent);

        // The same id at the same size updates in place
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 2, large.Size, 0, Vector3.Zero)]);
        Assert.Same(mirrored, Assert.Single(_world.Slimes));

        // A client never splits: its mirror's health arrives from the server, so a snapshot that
        // drops it across the half line, and client frames after it, leave it one slime
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, large.MaxHealth, large.Size, 0, Vector3.Zero)]);
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 1, large.Size, 0, Vector3.Zero)]);
        for (var i = 0; i < 10; i++)
            _world.Update(Dt, null);
        Assert.Same(mirrored, Assert.Single(_world.Slimes));

        // An older server's 0 reads as the smallest size, which replaces the mirror (a size is for life)
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 2, 0, 0, Vector3.Zero)]);
        var replaced = Assert.Single(_world.Mobs);
        Assert.NotSame(mirrored, replaced);
        Assert.Equal(SlimeSize.Min, Assert.IsType<Slime>(replaced).Size);
    }

    [Fact]
    public void AMobMirrorTakesTheEffectMaskAndDropsUnknownBits()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;
        var burning = StatusEffectRegistry.Require("burning");
        var unknown = (byte)(~StatusEffectRegistry.KnownMask & 0xFF);

        // A fresh mirror spawns burning, carrying no bit the loaded content does not define
        var pos = new Vector3(4f, 60f, 4f);
        _world.ClientMirror.ApplyNetworkEntities(
            [new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 3, 0, 0, Vector3.Zero, (byte)(burning.Bit | unknown))]);
        var mirrored = Assert.Single(_world.Slimes);
        Assert.True(mirrored.Effects.IsActive(burning));
        Assert.Equal(burning.Bit, mirrored.Effects.Mask);

        // The same mirror updated in place takes the server's newest mask, here none
        _world.ClientMirror.ApplyNetworkEntities([new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 3, 0, 0, Vector3.Zero)]);
        Assert.Same(mirrored, Assert.Single(_world.Slimes));
        Assert.False(mirrored.Effects.IsActive(burning));
        Assert.Equal(0, mirrored.Effects.Mask);

        // ...and a mask of nothing but unknown bits stays empty
        _world.ClientMirror.ApplyNetworkEntities(
            [new NetEntity(NetEntityKind.Slime, 7, pos, 1f, 3, 0, 0, Vector3.Zero, unknown)]);
        Assert.Equal(0, mirrored.Effects.Mask);
    }

    [Fact]
    public void AClientNeverTicksAMirroredPlayerEffect()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;
        var burning = StatusEffectRegistry.Require("burning");
        Assert.True(burning.DamageShare > 0f && burning.TickSeconds > 0f);

        // The server says the player burns; the client shows it, and its physics reads the same set
        _world.ClientMirror.ApplyNetworkEffects(burning.Bit);
        Assert.True(_world.Camera.Effects!.IsActive(burning));
        var health = _world.Health;

        // Frames spanning several of the effect's tick intervals: the client deals itself no damage
        // and expires nothing, since only the server ticks effects and reports the health
        var frames = (int)Math.Ceiling(burning.TickSeconds * 3 / Dt);
        for (var i = 0; i < frames; i++)
            _world.Update(Dt, null);
        Assert.Equal(health, _world.Health);
        Assert.True(_world.Camera.Effects.IsActive(burning));

        // The server's next status, with the effect gone, clears it
        _world.ClientMirror.ApplyNetworkEffects(0);
        Assert.False(_world.Camera.Effects.IsActive(burning));
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
        _world.ClientMirror.ApplyNetworkEntities([tnt, cube, slime]);

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
        _world.ClientMirror.ApplyNetworkEntities([later, second, cube]);
        Assert.Equal(2, _world.PrimedTnt.Count);
        Assert.Contains(_world.PrimedTnt, t => ReferenceEquals(t, mirrored));
        Assert.Equal(later.Position, mirrored.Position);
        Assert.Equal(later.Health / 1000f, mirrored.FuseRemaining);
        Assert.Equal(tnt.Ammo / 1000f, mirrored.FuseDuration);
        Assert.Empty(_world.Mobs);

        // A cube the next snapshot no longer lists goes, while the one still listed stays itself
        _world.ClientMirror.ApplyNetworkEntities([later, cube]);
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
        _world.ClientMirror.ApplyNetworkEntities([]);
        Assert.Empty(_world.PrimedTnt);
        Assert.Empty(_world.Falling);

        // A world that is not a client never takes mirrors, which its own Update would tick and
        // detonate
        var local = new World(null!, null!, null!, null!, null!, null!, null, null);
        _others.Add(local);
        local.ClientMirror.ApplyNetworkEntities([tnt, cube]);
        Assert.Empty(local.PrimedTnt);
        Assert.Empty(local.Falling);
    }

    [Fact]
    public void IcebergsMirrorFromASnapshotWithoutSimulation()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;

        // Far from the camera, over nothing loaded: a mirror never collides or reads the cells
        var at = new Vector3(500.5f, 0.9f, -300.5f);
        var velocity = new Vector3(0.5f, 0f, -0.25f);
        const float scale = 1.1f;
        var entry = new NetEntity(NetEntityKind.Iceberg, 21, at, NetEntity.YawFromTurns(3), 99,
            NetEntity.PackIcebergAmmo(scale, 0f), 1, velocity);
        _world.ClientMirror.ApplyNetworkEntities([entry]);

        var mirrored = Assert.Single(_world.Icebergs);
        Assert.True(mirrored.IsMirror);
        Assert.Equal(1, mirrored.Variant);
        Assert.Equal(3, mirrored.Turns);
        Assert.Equal(scale, mirrored.Scale, 2);
        Assert.Equal(at, mirrored.Center);

        // Between snapshots it only dead-reckons along its velocity, never past the lead cap
        for (var i = 0; i < 30; i++)
            _world.Update(Dt, null);
        var lead = mirrored.Center - at;
        Assert.True(lead.X > 0f && lead.Z < 0f, $"the mirror did not follow its velocity ({lead})");
        Assert.True(lead.Length() <= velocity.Length() * IcebergConfig.MirrorMaxLeadSeconds + 1e-3f);

        // The next snapshot moves the same mirror
        var moved = at + new Vector3(1f, 0f, 0f);
        _world.ClientMirror.ApplyNetworkEntities([entry with { Position = moved }]);
        Assert.Same(mirrored, Assert.Single(_world.Icebergs));
        Assert.Equal(moved, mirrored.Center);

        // An empty snapshot removes it
        _world.ClientMirror.ApplyNetworkEntities([]);
        Assert.Empty(_world.Icebergs);
    }
}
