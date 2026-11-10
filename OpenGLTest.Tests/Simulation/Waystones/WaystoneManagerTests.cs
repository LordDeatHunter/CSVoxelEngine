using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.Tests.Interaction;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>
/// The waystone registry: a record exists only once a waystone is activated, its first discoverer is the only one who
/// may rename it, a waystone whose block goes is kept but lost (by a break or the sweep), a player's discovered list
/// grows only on a first activation and a server resends that player's inventory, and the registry survives a save.
/// </summary>
public sealed class WaystoneManagerTests
{
    public WaystoneManagerTests() => TestContent.EnsureLoaded();

    private static ushort Waystone => ContentRegistry.RequireBlock("waystone_surface").Id;

    private static (ChunkManager Manager, WaystoneManager Waystones) World()
    {
        var (manager, center) = MeshTestWorld.WalledArea(Vector3.Zero);
        center.SetBlockRaw(4, 4, 4, Waystone, 0);
        center.SetBlockRaw(4, 5, 4, BlockType.ById[Waystone]!.UpperHalfId, 0);
        return (manager, new WaystoneManager(new Random(5)));
    }

    [Fact]
    public void TheFirstActivatorNamesItAndIsTheOnlyOneWhoMayRenameIt()
    {
        var (manager, waystones) = World();

        var first = waystones.Discover(manager, 4, 4, 4, "alice", out var aliceFirst)!;
        var again = waystones.Discover(manager, 4, 4, 4, "bob", out var bobFirst)!;

        Assert.Same(first, again);
        Assert.True(aliceFirst);
        Assert.False(bobFirst);
        Assert.True(waystones.Discover(manager, 4, 4, 4, "alice", out var stillAlice) != null && stillAlice);

        var name = first.Name;
        Assert.False(waystones.TryRename(4, 4, 4, "bob", "Bob's Rock"));
        Assert.Equal(name, first.Name);
        Assert.True(waystones.TryRename(4, 4, 4, "alice", "  Home \n Base "));
        Assert.Equal("Home Base", first.Name);
        Assert.False(waystones.TryRename(4, 4, 4, "alice", " \t "));
        Assert.Equal("Home Base", first.Name);
    }

    [Fact]
    public void OnlyALoadedWaystoneCellGetsARecord()
    {
        var (manager, waystones) = World();

        Assert.Null(waystones.Discover(manager, 5, 5, 5, "alice", out _));
        Assert.Null(waystones.Discover(manager, 4, 4, 400, "alice", out _));
        Assert.Equal(0, waystones.Count);
        Assert.False(waystones.TryRename(5, 5, 5, "alice", "Nowhere"));
    }

    [Fact]
    public void AWaystoneWhoseBlockGoesIsKeptButLostAndCannotBeRenamed()
    {
        var (manager, waystones) = World();
        var record = waystones.Discover(manager, 4, 4, 4, "alice", out _)!;
        var version = waystones.Version;

        waystones.NotifyBlockRemoved(4, 4, 4);

        Assert.True(record.Lost);
        Assert.Equal(1, waystones.Count);
        Assert.True(waystones.Version > version);
        Assert.False(waystones.MayRename(4, 4, 4, "alice"));
    }

    [Fact]
    public void TheSweepLosesAWaystoneThatVanishedWithoutABreakButNotOneInAnUnloadedChunk()
    {
        var (manager, waystones) = World();
        var record = waystones.Discover(manager, 4, 4, 4, "alice", out _)!;
        waystones.Restore([new StationSaveData.WaystoneData { Cell = [4, 4, 400], Name = "Faraway", Discoverer = "bob" }]);

        waystones.Update(manager);
        Assert.False(record.Lost);

        manager.ChunkAt(4, 4, 4)!.SetBlockRaw(4, 4, 4, 0, 0);
        waystones.Update(manager);

        Assert.True(record.Lost);
        Assert.False(waystones.Peek(4, 4, 400)!.Lost);
    }

    [Fact]
    public void AWaystoneStandingAgainWhereALostOneWasIsANewWaystone()
    {
        var (manager, waystones) = World();
        waystones.Discover(manager, 4, 4, 4, "alice", out _);
        waystones.NotifyBlockRemoved(4, 4, 4);

        var fresh = waystones.Discover(manager, 4, 4, 4, "bob", out var bobFirst)!;

        Assert.False(fresh.Lost);
        Assert.True(bobFirst);
        Assert.False(waystones.MayRename(4, 4, 4, "alice"));
    }

    [Fact]
    public void APlayersListGrowsOnlyOnItsFirstActivationAndKeepsItsOwnRename()
    {
        var (manager, waystones) = World();
        var alice = new PlayerContext(Inventory.Empty()) { Name = "Alice" };
        var bob = new PlayerContext(Inventory.Empty()) { Name = "Bob" };

        var first = waystones.DiscoverFor(manager, alice, 4, 4, 4)!.Value;
        var original = first.Record.Name;
        var repeat = waystones.DiscoverFor(manager, alice, 4, 4, 4)!.Value;
        var other = waystones.DiscoverFor(manager, bob, 4, 4, 4)!.Value;

        Assert.True(first is { NewToPlayer: true, FirstDiscoverer: true });
        Assert.False(repeat.NewToPlayer);
        Assert.True(other is { NewToPlayer: true, FirstDiscoverer: false });
        Assert.True(bob.Inventory.Waystones.Contains(4, 4, 4));

        Assert.False(waystones.TryRenameFor(bob, 4, 4, 4, "Bob's"));
        Assert.True(waystones.TryRenameFor(alice, 4, 4, 4, "Alice's"));
        Assert.True(alice.Inventory.Waystones.TryGetName(4, 4, 4, out var aliceKnows) && aliceKnows == "Alice's");
        // Bob keeps the name he found it under; the registry, not his list, carries the rename to him
        Assert.True(bob.Inventory.Waystones.TryGetName(4, 4, 4, out var bobKnows));
        Assert.Equal(original, bobKnows);
        Assert.Equal("Alice's", other.Record.Name);
    }

    [Fact]
    public void TheLocalPlayerAndEveryNamedPlayerAreDistinctDiscoverers()
    {
        var local = WaystoneManager.DiscovererOf(new PlayerContext(Inventory.Empty()));
        foreach (var name in new[] { "local", "Local", "@local", "Player" })
            Assert.NotEqual(local, WaystoneManager.DiscovererOf(new PlayerContext(Inventory.Empty()) { Name = name }));
        Assert.Equal(WaystoneManager.DiscovererOf(new PlayerContext(Inventory.Empty()) { Name = "Alice" }),
            WaystoneManager.DiscovererOf(new PlayerContext(Inventory.Empty()) { Name = "alice" }));
    }

    [Fact]
    public void TheRegistrySurvivesASaveAndABrokenEntryIsSkipped()
    {
        var (manager, waystones) = World();
        waystones.Discover(manager, 4, 4, 4, "alice", out _);
        waystones.TryRename(4, 4, 4, "alice", "Home");
        waystones.Restore([new StationSaveData.WaystoneData { Cell = [9, 9, 9], Name = "Gone", Discoverer = "bob", Lost = true }]);
        var saved = StationSaveCodec.TryParse(StationSaveCodec.ToBytes(new StationSaveData { Waystones = waystones.Capture() }))!;
        saved.Waystones!.Add(new StationSaveData.WaystoneData { Cell = [1, 2], Name = "Bad", Discoverer = "x" });
        saved.Waystones.Add(new StationSaveData.WaystoneData { Cell = [1, 2, 3], Name = "  ", Discoverer = "x" });
        saved.Waystones.Add(new StationSaveData.WaystoneData { Cell = [1, 2, 3], Name = "Nobody" });

        var restored = new WaystoneManager();
        Assert.Equal(3, restored.Restore(saved.Waystones));

        Assert.Equal(waystones.Count, restored.Count);
        foreach (var (cell, record) in waystones.Records)
        {
            var back = restored.Peek(cell.X, cell.Y, cell.Z)!;
            Assert.Equal((record.Name, record.FirstDiscoverer, record.Lost), (back.Name, back.FirstDiscoverer, back.Lost));
        }
    }

    [Fact]
    public void ALoneHalfIsNoWaystoneAndTheSweepLosesOneMissingEitherHalf()
    {
        foreach (var gone in new[] { 4, 5 })
        {
            var (manager, waystones) = World();
            var record = waystones.Discover(manager, 4, 4, 4, "alice", out _)!;

            // A blast takes one half without a break
            manager.ChunkAt(4, gone, 4)!.SetBlockRaw(4, gone, 4, 0, 0);
            Assert.False(WaystoneManager.IsWaystoneAt(manager, 4, 4, 4));
            waystones.Update(manager);

            Assert.True(record.Lost);
            Assert.Null(waystones.Discover(manager, 4, 4, 4, "bob", out _));
        }
    }

    [Fact]
    public void EitherHalfOfAWaystoneNamesItsLowerCellAndNoOtherBlockDoes()
    {
        var upper = BlockType.ById[Waystone]!.UpperHalfId;
        Assert.True(WaystoneManager.TryGetWaystoneCell(Waystone, 3, 7, -2, out var fromLower));
        Assert.True(WaystoneManager.TryGetWaystoneCell(upper, 3, 8, -2, out var fromUpper));
        Assert.Equal((3, 7, -2), fromLower);
        Assert.Equal(fromLower, fromUpper);
        Assert.False(WaystoneManager.TryGetWaystoneCell(ContentRegistry.RequireBlock("stone").Id, 3, 7, -2, out _));
        var doorTop = BlockType.Types.Values.First(t => t.LowerHalfId != 0 && !BlockType.ById[t.LowerHalfId]!.Waystone);
        Assert.False(WaystoneManager.TryGetWaystoneCell(doorTop.Id, 3, 8, -2, out _));
    }

    // A waystone standing two cells north of the player on the plateau; returns its lower cell
    private static (int X, int Y, int Z) StandWaystone(SupportTestWorld world)
    {
        int x = 8, y = world.Top + 1, z = 6;
        world.Load(x, y, z);
        world.Set(x, y, z, Waystone);
        world.Set(x, y + 1, z, BlockType.ById[Waystone]!.UpperHalfId);
        return (x, y, z);
    }

    [Fact]
    public void ARightClickOnEitherHalfOpensTheWaystoneAtItsLowerCell()
    {
        using var world = new SupportTestWorld();
        var (x, y, z) = StandWaystone(world);
        var eye = world.Player.Context.Eye;
        foreach (var target in new[] { new Vector3(x + 0.5f, y + 0.6f, z + 0.5f), new Vector3(x + 0.5f, y + 1.3f, z + 0.5f) })
        {
            world.Player.Aim(eye, Vector3.Normalize(target - eye));
            Assert.True(world.World.TryUseSelected(VoxelEngine.World.InteractionReach, world.Player.Context, out var outcome));
            Assert.Equal(RightClickOutcome.Waystone, outcome);
            Assert.Equal(new Vector3(x, y, z), world.World.LastWaystoneCell);
        }
    }

    [Fact]
    public void MiningEitherHalfTakesTheWaystoneDownLosesItOnceAndKeepsItInItsDiscoverersList()
    {
        foreach (var upperFirst in new[] { true, false })
        {
            using var world = new SupportTestWorld();
            var (x, y, z) = StandWaystone(world);
            var cell = new Vector3(x, y, z);

            world.Player.InventoryChanged = false;
            var found = world.World.DiscoverWaystoneFor(world.Player.Context, cell)!.Value;
            Assert.True(found.NewToPlayer);
            Assert.True(world.Player.InventoryChanged);

            world.Player.InventoryChanged = false;
            Assert.False(world.World.DiscoverWaystoneFor(world.Player.Context, cell)!.Value.NewToPlayer);
            Assert.False(world.Player.InventoryChanged);

            var version = world.World.Waystones.Version;
            Assert.True(world.Break(x, upperFirst ? y + 1 : y, z));

            Assert.Equal((BlockType.Air, BlockType.Air), (world.IdAt(x, y, z), world.IdAt(x, y + 1, z)));
            Assert.True(world.World.Waystones.Peek(x, y, z)!.Lost);
            Assert.Equal(version + 1, world.World.Waystones.Version);
            Assert.Null(world.World.Waystones.Peek(x, y + 1, z));
            Assert.True(world.Player.Inventory.Waystones.Contains(x, y, z));
            Assert.Null(world.World.DiscoverWaystoneFor(world.Player.Context, cell));
        }
    }
}
