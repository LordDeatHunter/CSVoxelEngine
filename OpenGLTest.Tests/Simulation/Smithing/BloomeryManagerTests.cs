using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>
/// The bloomery's rules: what it takes and in what order, how a run turns ore into blooms at any heat from the band up
/// and runs back under it, how a bellows against a tuyere heats it, what survives a save and the wire, and the
/// integrity sweep. The run is driven the way a player drives it: heat is held where the test wants it each step.
/// </summary>
public sealed class BloomeryManagerTests
{
    public BloomeryManagerTests() => TestContent.EnsureLoaded();

    private const float Step = 0.25f;
    private static readonly Vector3 Anchor = new(2, 2, 2);

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");
    private static Item Coal => ContentRegistry.RequireItem("coal");

    private static ushort OreId => Bloomery.Bloomery!.OreId;

    private static float Good => (BloomeryConfig.BandMin + 1f) * 0.5f;

    // A fall that works the bellows: a jump on the spot
    private const float JumpDrop = 1.1f;

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

    // Every cell around the volume (one ring out on the sides, any height in it) and whether a bellows there works it
    private static IEnumerable<((int X, int Y, int Z) Cell, bool Works)> Ring(int facing)
    {
        var size = Bloomery.MultiblockSize;
        for (var dx = -1; dx <= size; dx++)
        for (var dy = 0; dy < size; dy++)
        for (var dz = -1; dz <= size; dz++)
        {
            var inside = dx >= 0 && dx < size && dz >= 0 && dz < size;
            if (inside) continue;
            var cell = ((int)Anchor.X + dx, (int)Anchor.Y + dy, (int)Anchor.Z + dz);
            yield return (cell, Bloomery.Bloomery!.IsTuyere((dx, dy, dz), facing, size));
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
    public void AFireKeptAtTheBandTurnsEveryOreUnitIntoABloom()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 2, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));

        Run(state, BloomeryConfig.SecondsPerOre * 2 + 1f, Good);

        Assert.Equal(0, state.Ore);
        Assert.Equal(2, state.Blooms);
    }

    [Fact]
    public void AMaxedFireStillYieldsBloomsAndBurnsNoFasterThanACalmOne()
    {
        var (_, bloomeries, _) = Built();
        var maxed = Loaded(bloomeries, ore: 2, fuel: 4);
        Assert.True(bloomeries.Ignite(Anchor));
        var calm = new BloomeryManager.BloomeryState
            { Profile = maxed.Profile, Lit = true, Ore = 2, BurnLeft = maxed.BurnLeft, FuelSeconds = maxed.FuelSeconds, Fuel = maxed.Fuel };

        Run(maxed, BloomeryConfig.SecondsPerOre * 2 + 1f, 1f);
        Run(calm, BloomeryConfig.SecondsPerOre * 2 + 1f, Good);

        Assert.Equal(2, maxed.Blooms);
        Assert.Equal(calm.Blooms, maxed.Blooms);
        Assert.Equal(calm.Fuel, maxed.Fuel);
        Assert.Equal(calm.BurnLeft, maxed.BurnLeft, 3);
    }

    [Fact]
    public void AColdFireRunsTheUnitBackFasterTheColderAndLosesNoOre()
    {
        var (_, bloomeries, _) = Built();
        var cool = Loaded(bloomeries, ore: 1, fuel: 4);
        Assert.True(bloomeries.Ignite(Anchor));
        var cold = new BloomeryManager.BloomeryState
            { Profile = cool.Profile, Lit = true, Ore = 1, BurnLeft = 1000f };

        // Both half smelted, then left under the band
        Run(cool, BloomeryConfig.SecondsPerOre * 0.5f, Good);
        cold.UnitProgress = cool.UnitProgress;
        var halfway = cool.UnitProgress;
        Run(cool, 1f, BloomeryConfig.BandMin * 0.8f);
        Run(cold, 1f, BloomeryConfig.BandMin * 0.2f);

        Assert.True(cool.UnitProgress < halfway);
        Assert.True(cold.UnitProgress < cool.UnitProgress);

        // Long enough cold and the unit is back at the start, never under it, its ore still there
        Run(cool, BloomeryConfig.SecondsPerOre * 10f, 0f);
        Assert.Equal(0f, cool.UnitProgress);
        Assert.Equal(1, cool.Ore);
        Assert.Equal(0, cool.Blooms);
    }

    [Fact]
    public void WithoutLandingsTheHeatDecaysAndWhenTheFuelIsGoneTheFireGoesOutCold()
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
    public void NewOreWaitsUntilTheLastRunsOutputIsCollected()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));
        Run(state, BloomeryConfig.SecondsPerOre + 1f, Good);
        Assert.Equal(1, state.Blooms);

        Assert.False(bloomeries.TryAddOre(Anchor, OreId));

        var output = new List<ushort>();
        Assert.True(bloomeries.TryCollect(Anchor, output));
        Assert.Equal([Bloomery.Bloomery!.BloomId], output);
        Assert.False(bloomeries.TryCollect(Anchor, output));
        Assert.True(bloomeries.TryAddOre(Anchor, OreId));
    }

    [Fact]
    public void OnlyABellowsAgainstATuyereHeatsTheBloomeryAndNeverFasterThanItsCooldown()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        var ring = Ring(0).ToList();
        var tuyere = ring.First(r => r.Works).Cell;
        Assert.Contains(ring, r => !r.Works);

        // Unlit: nothing to heat
        Assert.False(bloomeries.PressBellows(tuyere, JumpDrop));

        Assert.True(bloomeries.Ignite(Anchor));
        state.Heat = 0.3f;
        foreach (var (cell, _) in ring.Where(r => !r.Works))
            Assert.False(bloomeries.PressBellows(cell, JumpDrop));
        Assert.Equal(0.3f, state.Heat);

        Assert.True(bloomeries.PressBellows(tuyere, JumpDrop));
        Assert.True(state.Heat > 0.3f);

        var pumped = state.Heat;
        Assert.False(bloomeries.PressBellows(tuyere, JumpDrop));
        Assert.Equal(pumped, state.Heat);

        BloomeryManager.Tick(state, BloomeryConfig.PumpCooldownSeconds + 0.01f);
        Assert.True(bloomeries.PressBellows(tuyere, JumpDrop));
    }

    [Fact]
    public void AFartherFallOnTheBellowsHeatsMoreAndASettleNotAtAll()
    {
        var (_, bloomeries, _) = Built();
        var state = Loaded(bloomeries, ore: 1, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));
        var tuyere = Ring(0).First(r => r.Works).Cell;

        state.Heat = 0.2f;
        Assert.False(bloomeries.PressBellows(tuyere, BloomeryConfig.BellowsMinFallBlocks * 0.5f));
        Assert.Equal(0.2f, state.Heat);

        Assert.True(bloomeries.PressBellows(tuyere, JumpDrop));
        var fromJump = state.Heat - 0.2f;
        state.Heat = 0.2f;
        state.PumpCooldown = 0f;
        Assert.True(bloomeries.PressBellows(tuyere, JumpDrop + 3f));
        Assert.True(state.Heat - 0.2f > fromJump);
    }

    [Fact]
    public void TheTuyeresTurnWithTheStructure()
    {
        // Each facing keeps the same number of tuyeres, and at least one of them moves to another cell
        var north = Ring(0).Where(r => r.Works).Select(r => r.Cell).ToHashSet();
        Assert.NotEmpty(north);
        for (var facing = 1; facing < 4; facing++)
        {
            var turned = Ring(facing).Where(r => r.Works).Select(r => r.Cell).ToHashSet();
            Assert.Equal(north.Count, turned.Count);
            Assert.False(turned.SetEquals(north), $"facing {facing} should move the tuyeres");

            var (_, bloomeries, _) = Built(facing);
            Loaded(bloomeries, ore: 1, fuel: 1);
            Assert.True(bloomeries.Ignite(Anchor));
            Assert.True(bloomeries.PressBellows(turned.First(), JumpDrop));
        }
    }

    [Fact]
    public void ARunSurvivesASaveAndTheWire()
    {
        var (_, bloomeries, _) = Built(facing: 2);
        var state = Loaded(bloomeries, ore: 3, fuel: 2);
        Assert.True(bloomeries.Ignite(Anchor));
        Run(state, 5f, Good);
        state.Blooms = 1;

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
