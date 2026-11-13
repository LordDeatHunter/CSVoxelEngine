using System.Linq;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Crucibles;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Rules of a crucible standing on the ground (<see cref="StandingCrucibleManager"/>): it keeps the vessel's own
/// metal from the moment it is set down to the moment it is lifted, the saves and the station snapshot carry it across
/// unchanged, and a stand whose block is gone hands the vessel over as a drop.
/// </summary>
public sealed class StandingCrucibleManagerTests
{
    private static readonly (int X, int Y, int Z) Cell = (4, 1, 4);

    public StandingCrucibleManagerTests() => TestContent.EnsureLoaded();

    private static byte Copper => ContentRegistry.MoltenMaterialByName("copper")!.Id;
    private static ushort CrucibleId => ContentRegistry.RequireItem("crucible").Id;
    private static ushort Stand => ContentRegistry.RequireBlock(StandingCrucibleManager.StandBlock).Id;

    private static ItemStack Vessel(int molten, int pending)
    {
        var stack = new ItemStack(7, CrucibleId);
        stack.Set(new CrucibleComponent(Copper, molten, pending));
        return stack;
    }

    [Fact]
    public void ASetDownCrucibleKeepsItsMetalUntilItIsLifted()
    {
        var manager = new StandingCrucibleManager();
        manager.Place(Cell, Vessel(300, 100));

        Assert.Equal(400, manager.Peek(Cell)!.Get<CrucibleComponent>().TotalMl);
        var lifted = manager.Take(Cell);
        Assert.Equal(300, lifted.Get<CrucibleComponent>().MoltenMl);
        Assert.Equal(100, lifted.Get<CrucibleComponent>().PendingMl);
        Assert.Null(manager.Peek(Cell));
    }

    [Fact]
    public void ALostEntryStillGivesAnEmptyCrucibleBack()
    {
        var lifted = new StandingCrucibleManager().Take(Cell);
        Assert.Equal(CrucibleId, lifted.DefinitionId);
        Assert.True(lifted.Get<CrucibleComponent>().IsEmpty);
    }

    [Fact]
    public void TheSaveAndTheWireCarryTheMetalAcrossUnchanged()
    {
        var manager = new StandingCrucibleManager();
        manager.Place(Cell, Vessel(250, 50));

        var restored = new StandingCrucibleManager();
        Assert.Equal(0, restored.Restore(manager.Capture()));
        Assert.Equal(300, restored.Peek(Cell)!.Get<CrucibleComponent>().TotalMl);

        using var writer = new PacketWriter(NetMessage.StationState);
        manager.WriteNet(writer);
        var mirror = new StandingCrucibleManager();
        mirror.Place((9, 9, 9), Vessel(1, 0));
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        mirror.ReadNet(reader);
        Assert.Null(mirror.Peek((9, 9, 9)));
        Assert.Equal(250, mirror.Peek(Cell)!.Get<CrucibleComponent>().MoltenMl);
    }

    [Fact]
    public void AnEntryThatNoLongerResolvesIsSkippedNotRestored()
    {
        var data = new StandingCrucibleManager();
        data.Place(Cell, Vessel(10, 0));
        var saved = data.Capture();
        saved[0].Item = "no_such_item";

        var restored = new StandingCrucibleManager();
        Assert.Equal(1, restored.Restore(saved));
        Assert.False(restored.HasAny);
    }

    [Fact]
    public void AStandWhoseBlockIsGoneDropsTheVesselAndOneWhoseBlockStandsKeepsIt()
    {
        var chunks = new ChunkManager(null!);
        StampedTree.WriteCell(chunks, Cell, Stand, 0);
        var manager = new StandingCrucibleManager();
        manager.Place(Cell, Vessel(200, 0));
        var drops = new DropManager();

        manager.Update(chunks, drops);
        Assert.True(manager.HasAny);
        Assert.Empty(drops.Drops);

        StampedTree.WriteCell(chunks, Cell, 0, 0);
        manager.Update(chunks, drops);
        Assert.False(manager.HasAny);
        var drop = Assert.Single(drops.Drops);
        Assert.Equal(200, drop.Instance!.Get<CrucibleComponent>().MoltenMl);
    }
}
