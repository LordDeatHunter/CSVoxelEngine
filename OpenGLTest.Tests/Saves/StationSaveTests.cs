using System.Linq;
using System.Numerics;
using System.Text;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// What the station registries write to <c>stations.json</c> comes back whole into fresh managers
/// (fires and their cooking, quern inputs and spins, kiln fuel, fire, crucible and melt, cast
/// progress), a pour comes back paused, an entry whose content no longer exists is dropped without
/// taking the rest down, and a damaged file parses to nothing.
/// </summary>
public sealed class StationSaveTests
{
    public StationSaveTests() => TestContent.EnsureLoaded();

    private static byte Mudball => ContentRegistry.RequireItem("mudball").Id;
    private static byte MalachiteChunk => ContentRegistry.RequireItem("malachite_ore").Id;
    private static Item Coal => ContentRegistry.RequireItem("coal");
    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static byte Copper => ContentRegistry.MoltenMaterialByName("copper")!.Id;

    private sealed record Stations(CampfireManager Campfires, QuernManager Querns, KilnManager Kilns,
        CastingManager Casting)
    {
        public static Stations Empty() => new(new CampfireManager(), new QuernManager(), new KilnManager(),
            new CastingManager());

        public StationSaveData Capture() => new()
        {
            Campfires = Campfires.Capture(),
            Querns = Querns.Capture(),
            Kilns = Kilns.Capture(),
            Casts = Casting.Capture(),
        };

        public int Restore(StationSaveData data) =>
            Campfires.Restore(data.Campfires) + Querns.Restore(data.Querns) + Kilns.Restore(data.Kilns)
            + Casting.Restore(data.Casts);
    }

    private static Stations Running()
    {
        var running = Stations.Empty();
        Assert.True(running.Campfires.Ignite(1, 2, 3));
        Assert.True(running.Campfires.TryPlaceFood(1, 2, 3, Mudball));
        Assert.True(running.Querns.TryInsert(5, 2, 3, MalachiteChunk));
        running.Querns.StartSpin(5, 2, 3);
        running.Kilns.Ensure(new Vector3(8, 2, 3), 2, Kiln.Id);
        Assert.True(running.Kilns.TryAddFuel(new Vector3(8, 2, 3), Coal));
        Assert.True(running.Kilns.Ignite(new Vector3(8, 2, 3)));

        var moldId = ContentRegistry.RequireBlock("fired_mold_pickaxe").Id;
        var stack = CrucibleInventory.Add(Inventory.Empty(), ContentRegistry.RequireItem("crucible").Id,
            new CrucibleComponent(Copper, 500, 0));
        Assert.NotNull(stack);
        Assert.True(running.Casting.TryStartPour(new Vector3(12, 2, 3), moldId, stack!));
        return running;
    }

    private static Stations ThroughTheFile(Stations from, out int skipped)
    {
        var parsed = StationSaveCodec.TryParse(StationSaveCodec.ToBytes(from.Capture()));
        Assert.NotNull(parsed);
        var into = Stations.Empty();
        skipped = into.Restore(parsed!);
        return into;
    }

    [Fact]
    public void EveryStationComesBackFromTheFileAsItWas()
    {
        var before = Running();
        var after = ThroughTheFile(before, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(before.Campfires.LitCells, after.Campfires.LitCells);
        Assert.Equal(before.Campfires.CookingView().ToList(), after.Campfires.CookingView().ToList());
        Assert.Equal(before.Querns.QuernView().ToList(), after.Querns.QuernView().ToList());
        Assert.True(after.Querns.IsSpinning(5, 2, 3));
        Assert.Equal(before.Kilns.LitCells, after.Kilns.LitCells);
        var original = before.Kilns.StateAt(8, 2, 3)!;
        var kiln = after.Kilns.StateAt(8, 2, 3);
        Assert.NotNull(kiln);
        Assert.Equal(original.Facing, kiln!.Facing);
        Assert.Equal(original.AnchorId, kiln.AnchorId);
        Assert.Equal(original.Lit, kiln.Lit);
        Assert.Equal(original.Fuel, kiln.Fuel);
        Assert.Equal(original.FuelItemId, kiln.FuelItemId);
        Assert.Equal(original.BurnLeft, kiln.BurnLeft);
        Assert.Equal(original.Contents, kiln.Contents);
        Assert.Equal(before.Casting.CastingView().ToList(), after.Casting.CastingView().ToList());
    }

    [Fact]
    public void ACrucibleAndItsMeltSurviveTheFile()
    {
        var before = Stations.Empty();
        var anchor = new Vector3(8, 2, 3);
        before.Kilns.Ensure(anchor, 0, Kiln.Id);
        Assert.True(before.Kilns.TryPlaceCrucible(anchor, ContentRegistry.RequireItem("crucible").Id,
            new CrucibleComponent(Copper, 200, 300, MalachiteChunk)));

        var after = ThroughTheFile(before, out _);
        var original = before.Kilns.StateAt(8, 2, 3)!;
        var restored = after.Kilns.StateAt(8, 2, 3)!;
        Assert.True(restored.HasCrucible);
        Assert.Equal(original.CrucibleItemId, restored.CrucibleItemId);
        Assert.Equal(original.Contents, restored.Contents);
        Assert.Equal(original.MeltLeft, restored.MeltLeft);
    }

    [Fact]
    public void ARestoredPourIsPausedUntilSomeoneResumesIt()
    {
        var before = Running();
        before.Casting.StateAt(12, 2, 3)!.FilledMl = 40;
        Assert.True(before.Casting.StateAt(12, 2, 3)!.Pouring);

        var after = ThroughTheFile(before, out _);
        var cast = after.Casting.StateAt(12, 2, 3);
        Assert.NotNull(cast);
        Assert.False(cast!.Pouring);
        Assert.Null(cast.PourSource);
        Assert.Equal(40, cast.FilledMl);
    }

    [Fact]
    public void AnEntryWhoseContentIsGoneIsDroppedAndTheRestStays()
    {
        var data = Running().Capture();
        data.Querns![0].Input = "item/no_such_item";
        data.Kilns![0].Anchor = "block/no_such_block";
        data.Casts![0].Material = "no_such_metal";
        data.Campfires![0].Slots![0].Output = "item/no_such_item";

        var into = Stations.Empty();
        var skipped = into.Restore(data);

        Assert.Equal(4, skipped);
        // The campfire still burns without the unknown slot, and the quern is still there, empty
        Assert.Single(into.Campfires.LitCells);
        Assert.Empty(into.Campfires.CookingView());
        Assert.False(into.Querns.StateAt(5, 2, 3)!.HasInput);
        Assert.Null(into.Kilns.StateAt(8, 2, 3));
        Assert.Null(into.Casting.StateAt(12, 2, 3));
    }

    [Fact]
    public void OutOfRangeNumbersAreRefusedNotRestored()
    {
        var data = Running().Capture();
        data.Kilns![0].Fuel = ContentRegistry.RequireBlock("mud_brick_kiln").Kiln!.FuelCap + 1;
        data.Kilns[0].MoltenMl = -5;
        data.Querns![0].Cell = [1, 2];

        var into = Stations.Empty();
        into.Restore(data);

        Assert.Null(into.Kilns.StateAt(8, 2, 3));
        Assert.False(into.Querns.HasAny);
    }

    [Fact]
    public void ADamagedOrMissingFileParsesToNothing()
    {
        Assert.Null(StationSaveCodec.TryParse(null));
        Assert.Null(StationSaveCodec.TryParse(Encoding.UTF8.GetBytes("null")));
        Assert.Null(StationSaveCodec.TryParse(Encoding.UTF8.GetBytes("{ \"Kilns\": [ { \"Cell\":")));
        Assert.Null(StationSaveCodec.TryParse(Encoding.UTF8.GetBytes("not json")));

        // A file with only some of the lists is fine: the rest restore as empty
        var partial = StationSaveCodec.TryParse(Encoding.UTF8.GetBytes("{ \"Querns\": [] }"));
        Assert.NotNull(partial);
        Assert.Equal(0, Stations.Empty().Restore(partial!));
    }
}
