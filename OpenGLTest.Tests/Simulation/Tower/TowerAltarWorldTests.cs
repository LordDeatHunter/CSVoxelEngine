using System;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// A right-click on a tower's roof altar through the real chain (<see cref="RightClickAction.TryUseSelected(float, out RightClickOutcome)"/>,
/// <see cref="World.UseAltar"/>): the altar claims the click, the authoritative world begins the ritual once and then only
/// stirs, a held block falls through to placement, and a client only judges it while raising its plain Use intent for
/// the server to replay (the replay itself is covered by the loopback <c>GameServerTests</c>).
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerAltarWorldTests : IDisposable
{
    private World? _world;

    public TowerAltarWorldTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        WorldSeed.Apply(WorldSeed.Default);
    }

    // The altar sits three blocks in front of the eye, up in open air facing -Z
    private static readonly Vector3 Eye = new(0.5f, 300.5f, 0.5f);
    private static readonly Vector3 AltarCell = new(0f, 300f, -3f);

    // A two-floor tower whose roof altar is AltarCell, with every door open unless asked otherwise
    private static TowerRecord Record(bool open)
    {
        var record = TowerRecord.Build((9, 9), (int)AltarCell.X, (int)AltarCell.Z, 12,
        [
            (288, 294, Array.Empty<(int, int, int)>(), Array.Empty<(int, int, int)>()),
            (294, 299, Array.Empty<(int, int, int)>(), Array.Empty<(int, int, int)>()),
        ], hasSite: false);
        Assert.Equal(((int)AltarCell.X, (int)AltarCell.Y, (int)AltarCell.Z), record.AltarCell);
        foreach (var floor in record.Floors) floor.DoorOpen = open;
        return record;
    }

    private (World World, TowerRecord Record) WorldWithAltar(bool clientMode, bool open = true)
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: clientMode);
        if (clientMode) _world.GetChunkManager().Headless = true;
        _world.Camera.TestTeleport(Eye, yaw: -90f, pitch: 0f);
        var manager = _world.GetChunkManager();
        var chunkPos = VoxelCoords.ChunkPosOf(AltarCell);
        // Nothing generates up there, so the chunk holding the altar is ours
        var chunk = new Chunk(null!, chunkPos, generateBlockData: false) { IsBlockDataReady = true };
        manager.Chunks[chunkPos] = chunk;
        chunk.SetBlockRaw((int)AltarCell.X - (int)chunkPos.X * Chunk.Size, (int)AltarCell.Y - (int)chunkPos.Y * Chunk.Size,
            (int)AltarCell.Z - (int)chunkPos.Z * Chunk.Size, TowerPalette.AltarBlock, 0);
        return (_world, _world.Towers.Track(Record(open)));
    }

    private static RightClickOutcome Click(World world)
    {
        world.LeftClick.UpdateSelection();
        world.RightClick.TryUseSelected(World.InteractionReach, out var outcome);
        return outcome;
    }

    [Fact]
    public void TheAuthoritativeWorldBeginsTheRitualOnceThenStirs()
    {
        var (world, record) = WorldWithAltar(clientMode: false);

        Assert.Equal(RightClickOutcome.Altar, Click(world));
        Assert.Equal(AltarCell, world.LastAltarCell);
        Assert.Equal(AltarVerdict.Begin, world.RightClick.LastAltarVerdict);
        Assert.Equal(TowerAltarState.Ritual, record.AltarState);

        Assert.Equal(RightClickOutcome.Altar, Click(world));
        Assert.Equal(AltarVerdict.Stirring, world.RightClick.LastAltarVerdict);
        Assert.Equal(TowerAltarState.Ritual, record.AltarState);
    }

    [Fact]
    public void ASealedTowerClaimsTheClickButBeginsNothing()
    {
        var (world, record) = WorldWithAltar(clientMode: false, open: false);

        Assert.Equal(RightClickOutcome.Altar, Click(world));
        Assert.Equal(AltarVerdict.Sealed, world.RightClick.LastAltarVerdict);
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
    }

    [Fact]
    public void ABlockInHandPlacesAgainstTheAltarInstead()
    {
        var (world, record) = WorldWithAltar(clientMode: false);
        Assert.True(world.Inventory.Add(ContentRegistry.RequireBlock("stone").Id));
        for (var slot = 0; slot < 10 && world.Inventory.HeldItem is not BlockItem; slot++)
            world.Inventory.Select(slot);
        Assert.IsAssignableFrom<BlockItem>(world.Inventory.HeldItem);

        Assert.NotEqual(RightClickOutcome.Altar, Click(world));
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
    }

    [Fact]
    public void AClientOnlyJudgesTheAltarAndRaisesAUseIntent()
    {
        var (world, record) = WorldWithAltar(clientMode: true);
        var intents = new System.Collections.Generic.List<WorldIntent>();
        world.IntentRaised += intents.Add;

        Assert.Equal(RightClickOutcome.Altar, Click(world));
        Assert.Equal(AltarVerdict.Begin, world.RightClick.LastAltarVerdict);
        // The prediction changes nothing: the server's replay of the Use intent begins the ritual
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
        Assert.Equal(AltarVerdict.Begin, world.UseAltar(AltarCell));
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
        var intent = Assert.Single(intents);
        Assert.Equal(NetIntentKind.Use, intent.Kind);
    }
}
