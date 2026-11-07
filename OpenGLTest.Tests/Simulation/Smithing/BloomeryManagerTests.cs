using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Stations;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>
/// The bloomery's rules: what it takes and in what order, how a run turns ore into a bloom or burns it to slag
/// depending on the heat the bellows keep, how the bellows reach it, what survives a save and the wire, and the
/// integrity sweep. The run is driven the way a player drives it: heat is held where the test wants it each step.
/// </summary>
public sealed class BloomeryManagerTests
{
    public BloomeryManagerTests() => TestContent.EnsureLoaded();

    private const float Step = 0.25f;
    private static readonly Vector3 Anchor = new(2, 2, 2);

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");
    private static Item Coal => ContentRegistry.RequireItem("coal");

    private static byte OreId => Bloomery.Bloomery!.OreId;

    private static float Mid => (BloomeryConfig.BandMin + BloomeryConfig.BandMax) * 0.5f;

    private static (ChunkManager Manager, BloomeryManager Bloomeries, DropManager Drops) Built(int facing = 0)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        Multiblock.Convert(manager, Anchor, facing, Bloomery, new HashSet<Vector3>());
        var bloomeries = new BloomeryManager();
        bloomeries.Ensure(Anchor, facing, Bloomery.Id);
        return (manager, bloomeries, new DropManager());
    }

    private static BloomeryManager.BloomeryState Loaded(BloomeryManager bloomeries, int ore, int fuel)
    {
        for (var i = 0; i < ore; i++) Assert.True(bloomeries.TryAddOre(Anchor, OreId));
        for (var i = 0; i < fuel; i++) Assert.True(bloomeries.TryAddFuel(Anchor, Coal));
        return bloomeries.StateAt(2, 2, 2)!;
    }

    // Runs the fire for `seconds`, holding the heat at `heat` before every step like a player working the bellows
    private static void Run(BloomeryManager.BloomeryState state, float seconds, float heat)
    {
        for (var t = 0f; t < seconds; t += Step)
        {
            state.Heat = heat;
            BloomeryManager.Tick(state, Step);
        }
    }

    [Fact]
    public void ALightRequiresBothOreAndFuelAndSpendsTheFirstFuelUnit()
    {
        var (_, bloomeries, _) = Built();
        Assert.False(bloomeries.CanIgnite(Anchor));

        Loaded(bloomeries, ore: 1, fuel: 0);
        Assert.False(bloomeries.CanIgnite(Anchor));

        Assert.True(bloomeries.TryAddFuel(Anchor, Coal));
        Assert.True(bloomeries.CanIgnite(Anchor));
        Assert.True(bloomeries.Ignite(Anchor));

        var state = bloomeries.StateAt(2, 2, 2)!;
        Assert.True(state.Lit);
        Assert.Equal(0, state.Fuel);
        Assert.True(state.Heat > 0f);
        Assert.False(bloomeries.CanIgnite(Anchor));
        Assert.Single(bloomeries.LitCells);
    }

    [Fact]
    public void ItTakesOnlyItsOreAndFuelUpToItsCaps()
    {
        var (_, bloomeries, _) = Built();
        var profile = Bloomery.Bloomery!;

        Assert.False(bloomeries.TryAddOre(Anchor, Coal.Id));
        Assert.False(bloomeries.TryAddFuel(Anchor, ItemRegistry.TryGet(OreId)!));

        for (var i = 0; i < profile.OreCap; i++) Assert.True(bloomeries.TryAddOre(Anchor, OreId));
        Assert.False(bloomeries.TryAddOre(Anchor, OreId));
        for (var i = 0; i < profile.FuelCap; i++) Assert.True(bloomeries.TryAddFuel(Anchor, Coal));
        Assert.False(bloomeries.TryAddFuel(Anchor, Coal));
    }

    [Fact]
    public void AFireKeptInTheBandTurnsEveryOreUnitIntoABloom()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 2, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));

        Run(state, BloomeryConfig.SecondsPerOre * 2 + 1f, Mid);

        Assert.Equal(0, state.Ore);
        Assert.Equal(2, state.Blooms);
        Assert.Equal(0, state.Slag);
    }

    [Fact]
    public void AColdFireStallsTheSmeltAndLosesNothing()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));

        Run(state, BloomeryConfig.SecondsPerOre * 3, BloomeryConfig.BandMin - 0.2f);

        Assert.Equal(1, state.Ore);
        Assert.Equal(0f, state.UnitProgress);
        Assert.Equal(0, state.Blooms + state.Slag);
    }

    [Fact]
    public void AnOverheatedUnitBurnsToSlagInsteadOfABloom()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 4);
        Assert.True(bloomeries.Ignite(Anchor));

        Run(state, BloomeryConfig.SecondsPerOre + 1f, 1f);

        Assert.Equal(0, state.Ore);
        Assert.Equal(0, state.Blooms);
        Assert.Equal(BloomeryConfig.SlagPerBurntUnit, state.Slag);
    }

    [Fact]
    public void ABriefOverheatDoesNotBurnTheUnit()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));

        Run(state, BloomeryConfig.BurnSeconds * 0.5f, 1f);
        Run(state, BloomeryConfig.SecondsPerOre, Mid);

        Assert.Equal(1, state.Blooms);
        Assert.Equal(0, state.Slag);
    }

    [Fact]
    public void WithoutPumpsTheHeatDecaysAndWhenTheFuelIsGoneTheFireGoesOutCold()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 1);
        Assert.True(bloomeries.Ignite(Anchor));
        var start = state.Heat;
        BloomeryManager.Tick(state, 1f);
        Assert.True(state.Heat < start);

        state.BurnLeft = 0.1f;
        Assert.True(BloomeryManager.Tick(state, 1f));
        Assert.False(state.Lit);
        Assert.Equal(0f, state.Heat);
    }

    [Fact]
    public void AnOverheatedFireEatsItsFuelFasterThanACalmOne()
    {
        var (_, bloomeries, _) = Built();
        var calm = Loaded(bloomeries, ore: 1, fuel: 4);
        Assert.True(bloomeries.Ignite(Anchor));
        var hot = new BloomeryManager.BloomeryState { Profile = calm.Profile, Lit = true, Ore = 1, BurnLeft = calm.BurnLeft };

        var calmBefore = calm.BurnLeft;
        var hotBefore = hot.BurnLeft;
        Run(calm, 4f, Mid);
        Run(hot, 4f, 1f);

        Assert.True(hotBefore - hot.BurnLeft > calmBefore - calm.BurnLeft);
    }

    [Fact]
    public void NewOreWaitsUntilTheLastRunsOutputIsCollected()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));
        Run(state, BloomeryConfig.SecondsPerOre + 1f, Mid);
        Assert.Equal(1, state.Blooms);

        Assert.False(bloomeries.TryAddOre(Anchor, OreId));

        var output = new List<byte>();
        Assert.True(bloomeries.TryCollect(Anchor, output));
        Assert.Equal([Bloomery.Bloomery!.BloomId], output);
        Assert.False(bloomeries.TryCollect(Anchor, output));
        Assert.True(bloomeries.TryAddOre(Anchor, OreId));
    }

    [Fact]
    public void ABellowsPumpHeatsOnlyALitBloomeryInRangeAndNotFasterThanItsCooldown()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        var center = Anchor + new Vector3(1f, 1f, 1f);

        Assert.False(bloomeries.Pump(center));

        Assert.True(bloomeries.Ignite(Anchor));
        state.Heat = 0.3f;
        Assert.False(bloomeries.Pump(center + new Vector3(BloomeryConfig.PumpRangeBlocks + 1f, 0f, 0f)));
        Assert.Equal(0.3f, state.Heat);

        Assert.True(bloomeries.Pump(center + new Vector3(1f, 0f, 0f)));
        Assert.True(state.Heat > 0.3f);

        var pumped = state.Heat;
        Assert.False(bloomeries.Pump(center));
        Assert.Equal(pumped, state.Heat);

        BloomeryManager.Tick(state, BloomeryConfig.PumpCooldownSeconds + 0.01f);
        Assert.True(bloomeries.Pump(center));
    }

    [Fact]
    public void ARunSurvivesASaveAndTheWire()
    {
        var (_, bloomeries, _) = Built(facing: 2);
        var state = Loaded(bloomeries, ore: 3, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));
        Run(state, 5f, Mid);
        state.Blooms = 1;
        state.Slag = 1;

        var saved = new BloomeryManager();
        Assert.Equal(0, saved.Restore(bloomeries.Capture()));
        AssertSame(state, saved.StateAt(2, 2, 2)!);

        using var writer = new PacketWriter(NetMessage.StationState);
        bloomeries.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new BloomeryManager();
        mirror.ReadNet(reader);
        AssertSame(state, mirror.StateAt(2, 2, 2)!);
        Assert.Single(mirror.LitCells);
    }

    private static void AssertSame(BloomeryManager.BloomeryState a, BloomeryManager.BloomeryState b)
    {
        Assert.Equal(a.AnchorId, b.AnchorId);
        Assert.Equal(a.Facing, b.Facing);
        Assert.Equal(a.Ore, b.Ore);
        Assert.Equal(a.Fuel, b.Fuel);
        Assert.Equal(a.FuelItemId, b.FuelItemId);
        Assert.Equal(a.Lit, b.Lit);
        Assert.Equal(a.Heat, b.Heat);
        Assert.Equal(a.UnitProgress, b.UnitProgress);
        Assert.Equal(a.Blooms, b.Blooms);
        Assert.Equal(a.Slag, b.Slag);
    }

    [Fact]
    public void ARestoredEntryWithOutOfRangeNumbersIsDropped()
    {
        var (_, bloomeries, _) = Built();
        Loaded(bloomeries, ore: 1, fuel: 1);
        var data = bloomeries.Capture();
        data[0].Ore = Bloomery.Bloomery!.OreCap + 1;

        var restored = new BloomeryManager();
        Assert.Equal(1, restored.Restore(data));
        Assert.False(restored.HasAny);
    }

    [Fact]
    public void ABlastedCellRevertsTheStructureAndDropsWhatItHeld()
    {
        var (manager, bloomeries, drops) = Built();
        Loaded(bloomeries, ore: 2, fuel: 1);
        var size = Bloomery.MultiblockSize;
        var blasted = Anchor + new Vector3(size - 1, size - 1, size - 1);
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)blasted.X, (int)blasted.Y, (int)blasted.Z);
        manager.ChunkAt((int)blasted.X, (int)blasted.Y, (int)blasted.Z)!.SetBlockRaw(lx, ly, lz, 0, 0);

        bloomeries.Update(0.1f, manager, drops);

        Assert.Null(bloomeries.StateAt(2, 2, 2));
        Assert.Equal(3, drops.Drops.Count);
        Assert.True(manager.TryGetCell((int)Anchor.X, (int)Anchor.Y, (int)Anchor.Z, out var id, out _));
        Assert.Equal(Bloomery.MultiblockSourceId, id);
    }
}
