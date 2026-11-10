using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Chests;

/// <summary>
/// Tower chests (BT-5): every tier a tower stamps has a loaded table, the tier map never steps back and each higher table
/// rolls rarer gear and no less of it, a chest rolls exactly its tier's table whatever its facing, the roll is a pure function
/// of cell, seed and tier, opening and breaking unopened agree with nothing held until then, and a tiered profile is refused
/// unless every table loads.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerChestTests
{
    public TowerChestTests() => TestContent.EnsureLoaded();

    private static ushort Tower => ContentRegistry.RequireBlock("tower_chest").Id;
    private static ChestProfile Profile => BlockType.ById[Tower]!.Chest!;

    /// <summary>Every tier a tower stamps: its floors, then the roof's treasure.</summary>
    private static IEnumerable<int> StampedTiers =>
        Enumerable.Range(0, WorldGenerationConfig.TowerMaxFloors).Append(TowerSite.TreasureTier);

    private static string Describe(ChestState c) => string.Join(",", Enumerable.Range(0, c.SlotCount)
        .Select(i => c[i]).Where(s => !s.IsEmpty).Select(s => $"{s.Id}x{s.Count}:{s.Instance?.ResolvedName}"));

    [Fact]
    public void EveryTierATowerStampsHasALoadedTable()
    {
        foreach (var tier in StampedTiers)
        {
            var name = Profile.TableFor(ChestProfile.TierState(tier));
            Assert.NotNull(name);
            Assert.NotNull(ContentRegistry.LootTableFor(name!));
        }
        Assert.NotEqual(Profile.TableFor(ChestProfile.TierState(WorldGenerationConfig.TowerMaxFloors - 1)),
            Profile.TableFor(ChestProfile.TierState(TowerSite.TreasureTier)));
    }

    [Fact]
    public void TheTierMapIsMonotoneAndHigherTablesAreRicher()
    {
        var sequence = new List<string>();
        for (var tier = 0; tier <= TowerSite.TreasureTier; tier++)
        {
            var name = Profile.TableFor(ChestProfile.TierState(tier))!;
            if (sequence.Count == 0 || sequence[^1] != name) sequence.Add(name);
        }
        Assert.Equal(sequence.Count, sequence.Distinct().Count());
        Assert.True(sequence.Count > 1, "the tiers should reach more than one table");

        const int samples = 600;
        var stats = new List<(string Name, Rarity Least, double GearPerRoll)>();
        foreach (var name in sequence)
        {
            var table = ContentRegistry.LootTableFor(name)!;
            Rarity? least = null;
            var gear = 0;
            var rolled = new List<LootStack>();
            for (var seed = 0; seed < samples; seed++)
            {
                rolled.Clear();
                table.RollStacks(new LootContext(0, 0, 0), rolled, new Random(seed));
                foreach (var stack in rolled)
                {
                    if (stack.Rarity is not { } rarity) continue;
                    Assert.IsType<GearItem>(ItemRegistry.TryGet(stack.Item));
                    gear += stack.Count;
                    if (least == null || rarity < least) least = rarity;
                }
            }
            Assert.True(least != null, $"{name} never rolled any gear");
            stats.Add((name, least!.Value, gear / (double)samples));
        }

        for (var i = 1; i < stats.Count; i++)
        {
            var (prevName, prevLeast, prevGear) = stats[i - 1];
            var (name, least, gearPerRoll) = stats[i];
            Assert.True(least > prevLeast, $"{name}'s least gear rarity {least} should be rarer than {prevName}'s {prevLeast}");
            Assert.True(gearPerRoll >= prevGear, $"{name} rolls {gearPerRoll:F2} gear a chest, fewer than {prevName}'s {prevGear:F2}");
        }
    }

    [Fact]
    public void AChestOfTierKRollsTierKsTable()
    {
        foreach (var tier in StampedTiers)
        {
            var plain = new ChestProfile(Profile.Slots, Profile.TableFor(ChestProfile.TierState(tier)));
            for (var facing = 0; facing < 4; facing++)
            for (var x = 0; x < 3; x++)
            {
                var state = (byte)(ChestProfile.TierState(tier) | facing);
                Assert.Equal(ChestProfile.TierOf(ChestProfile.TierState(tier)), ChestProfile.TierOf(state));
                Assert.Equal(Describe(ChestLoot.Materialize(plain, 0, x, 70, -12, 9)),
                    Describe(ChestLoot.Materialize(Profile, state, x, 70, -12, 9)));
            }
        }
    }

    [Fact]
    public void ATowerChestIsAPureFunctionOfCellSeedAndTier()
    {
        var state = ChestProfile.TierState(3);
        Assert.Equal(Describe(ChestLoot.Materialize(Profile, state, 10, 20, 30, 1)),
            Describe(ChestLoot.Materialize(Profile, state, 10, 20, 30, 1)));

        var contents = new HashSet<string>();
        for (var x = 0; x < 40; x++) contents.Add(Describe(ChestLoot.Materialize(Profile, state, x, 20, 30, 1)));
        Assert.True(contents.Count > 10, "different cells hold different loot");

        var low = ChestProfile.TierState(0);
        var high = ChestProfile.TierState(TowerSite.TreasureTier);
        Assert.Contains(Enumerable.Range(0, 5), x =>
            Describe(ChestLoot.Materialize(Profile, low, x, 5, 5, 1)) != Describe(ChestLoot.Materialize(Profile, high, x, 5, 5, 1)));
    }

    [Fact]
    public void ATowerChestRollsTheSameContentsOpenedAsBrokenUnopenedAndHoldsNoStateUntilUsed()
    {
        var state = (byte)(ChestProfile.TierState(5) | 2);

        var (manager, center) = MeshTestWorld.WalledArea(Vector3.Zero);
        center.SetBlockRaw(4, 4, 4, Tower, state);
        var chests = new ChestManager();
        Assert.Equal(0, chests.Count);
        Assert.Null(chests.Peek(4, 4, 4));

        var chest = chests.Open(manager, 4, 4, 4)!;
        var shown = Enumerable.Range(0, chest.SlotCount).Select(i => chest[i]).Where(s => !s.IsEmpty)
            .SelectMany(s => Enumerable.Repeat(s.Id, s.Count)).OrderBy(i => i).ToList();
        Assert.NotEmpty(shown);

        var broken = new ChestManager();
        var drops = new DropManager();
        broken.NotifyBlockRemoved(4, 4, 4, Profile, state, drops);
        var spilled = drops.Drops.Select(d => d.BlockId).OrderBy(i => i).ToList();

        Assert.Equal(shown, spilled);
    }

    [Fact]
    public void TiersAreRefusedUnlessEveryTableLoads()
    {
        const string path = "blocks/test_tiered_chest.json";
        var valid = Profile.TierTables!.ToArray();

        Exception Refused(BlockDefinition.ChestDefinition definition) => Assert.Throws<InvalidOperationException>(() =>
            BlockSchemaParser.ParseChest(definition, BlockRenderer.Model, path));

        Assert.Contains(path, Refused(new BlockDefinition.ChestDefinition { Slots = 18, Tiers = [valid[0], "no_such_table"] }).Message);
        Assert.Contains(path, Refused(new BlockDefinition.ChestDefinition { Slots = 18, Tiers = [] }).Message);
        Assert.Contains(path, Refused(new BlockDefinition.ChestDefinition { Slots = 18, Tiers = [valid[0], " "] }).Message);
        Assert.Contains(path, Refused(new BlockDefinition.ChestDefinition { Slots = 18, Loot = valid[0], Tiers = valid }).Message);

        var parsed = BlockSchemaParser.ParseChest(new BlockDefinition.ChestDefinition { Slots = 18, Tiers = valid }, BlockRenderer.Model, path)!;
        Assert.Equal(valid, parsed.TierTables);
        Assert.Null(parsed.LootTable);
    }
}
