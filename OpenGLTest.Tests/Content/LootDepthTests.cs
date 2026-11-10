using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// Loot depth: a seeded roll is a pure function of its seed, a nested table rolls its own pools in an entry's place,
/// a gear entry rolls a rarity inside its range, a pool can be limited to a Y band, and a malformed table is refused
/// naming its file. Plus the shipped ruin tables compile, nest and hand out gear of their tier.
/// </summary>
public sealed class LootDepthTests
{
    public LootDepthTests() => TestContent.EnsureLoaded();

    private static LootTableDefinition Parse(string json) =>
        JsonSerializer.Deserialize<LootTableDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static LootTable Compile(string name, string json) =>
        LootTableCompiler.CompileLootTable(name, Parse(json), $"data/loot/{name}.json");

    private static List<LootStack> Stacks(LootTable table, int seed, int y = 0)
    {
        var into = new List<LootStack>();
        table.RollStacks(new LootContext(0, y, 0), into, new Random(seed));
        return into;
    }

    [Fact]
    public void ASeededRollIsAPureFunctionOfItsSeed()
    {
        var table = ContentRegistry.LootTableFor("ruin_underground")!;
        for (var seed = 0; seed < 25; seed++)
            Assert.Equal(Stacks(table, seed), Stacks(table, seed));

        var all = Enumerable.Range(0, 60).Select(s => string.Join(",", Stacks(table, s))).Distinct().Count();
        Assert.True(all > 10, "different seeds should give different contents");
    }

    [Fact]
    public void TheIdListAndTheStackRollAgreeForTheSameSeed()
    {
        var table = ContentRegistry.LootTableFor("ruin_surface")!;
        for (var seed = 0; seed < 25; seed++)
        {
            var ids = new List<ushort>();
            table.Roll(new LootContext(0, 0, 0), ids, new Random(seed));
            var fromStacks = Stacks(table, seed).SelectMany(s => Enumerable.Repeat(s.Item, s.Count)).ToList();
            Assert.Equal(ids, fromStacks);
        }
    }

    [Fact]
    public void ANestedTableRollsItsOwnPoolsInTheEntrysPlace()
    {
        var inner = Compile("inner", """{ "pools": [ { "rolls": 1, "entries": [ { "item": "item/flint", "count": 3 } ] } ] }""");
        var outer = Compile("outer", """{ "pools": [ { "rolls": 2, "entries": [ { "table": "inner" } ] } ] }""");
        var tables = new Dictionary<string, LootTable> { ["inner"] = inner, ["outer"] = outer };
        LootTableCompiler.ResolveNested(tables, new Dictionary<string, string>());

        var stacks = Stacks(outer, 1);
        Assert.Equal(2, stacks.Count);
        Assert.All(stacks, s => Assert.Equal((ushort)ContentRegistry.RequireItem("flint").Id, s.Item));
        Assert.Equal(6, stacks.Sum(s => s.Count));
    }

    [Fact]
    public void ATableThatContainsItselfThroughAChainIsRefusedNamingItsFile()
    {
        var a = Compile("a", """{ "pools": [ { "entries": [ { "table": "b" } ] } ] }""");
        var b = Compile("b", """{ "pools": [ { "entries": [ { "table": "a" } ] } ] }""");
        var tables = new Dictionary<string, LootTable> { ["a"] = a, ["b"] = b };
        var paths = new Dictionary<string, string> { ["a"] = "data/loot/a.json", ["b"] = "data/loot/b.json" };

        var error = Assert.Throws<InvalidOperationException>(() => LootTableCompiler.ResolveNested(tables, paths));
        Assert.Contains("data/loot/", error.Message);

        var missing = Compile("m", """{ "pools": [ { "entries": [ { "table": "nowhere" } ] } ] }""");
        Assert.Throws<InvalidOperationException>(() =>
            LootTableCompiler.ResolveNested(new Dictionary<string, LootTable> { ["m"] = missing }, new Dictionary<string, string>()));
    }

    [Fact]
    public void AGearEntryRollsARarityInsideItsRangeAndOtherEntriesRollNone()
    {
        var table = Compile("gear", """
            { "pools": [ { "rolls": 4, "entries": [ { "item": "item/bronze_cap", "rarity": { "min": "uncommon", "max": "epic" } } ] },
                         { "entries": [ { "item": "item/flint" } ] } ] }
            """);
        var seen = new HashSet<Rarity>();
        for (var seed = 0; seed < 80; seed++)
            foreach (var stack in Stacks(table, seed))
            {
                if (stack.Item == ContentRegistry.RequireItem("flint").Id)
                {
                    Assert.Null(stack.Rarity);
                    continue;
                }
                Assert.InRange((int)stack.Rarity!.Value, (int)Rarity.Uncommon, (int)Rarity.Epic);
                Assert.Equal(1, stack.Count);
                seen.Add(stack.Rarity.Value);
            }
        Assert.True(seen.Count > 1, "a range should reach more than one tier over many rolls");
    }

    [Fact]
    public void ARarityOnAnythingButGearIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Compile("bad",
            """{ "pools": [ { "entries": [ { "item": "item/flint", "rarity": "rare" } ] } ] }"""));
        Assert.Contains("data/loot/bad.json", error.Message);
        Assert.Throws<InvalidOperationException>(() => Compile("bad",
            """{ "pools": [ { "entries": [ { "item": "item/bronze_cap", "rarity": "mythic" } ] } ] }"""));
        Assert.Throws<InvalidOperationException>(() => Compile("bad",
            """{ "pools": [ { "entries": [ { "item": "item/bronze_cap", "rarity": { "min": "epic", "max": "rare" } } ] } ] }"""));
    }

    [Fact]
    public void AnEntryMustBeExactlyOneKind()
    {
        Assert.Throws<InvalidOperationException>(() => Compile("bad",
            """{ "pools": [ { "entries": [ { "item": "item/flint", "table": "x" } ] } ] }"""));
        Assert.Throws<InvalidOperationException>(() => Compile("bad", """{ "pools": [ { "entries": [ { "weight": 1 } ] } ] }"""));
    }

    [Fact]
    public void APoolLimitedToAYBandOnlyRollsInsideIt()
    {
        var table = Compile("band", """{ "pools": [ { "minY": -100, "maxY": -50, "entries": [ { "item": "item/flint" } ] } ] }""");
        Assert.NotEmpty(Stacks(table, 1, y: -75));
        Assert.NotEmpty(Stacks(table, 1, y: -100));
        Assert.NotEmpty(Stacks(table, 1, y: -50));
        Assert.Empty(Stacks(table, 1, y: -49));
        Assert.Empty(Stacks(table, 1, y: -101));
        Assert.Throws<InvalidOperationException>(() => Compile("bad", """{ "pools": [ { "minY": 5, "maxY": 1, "entries": [ { "item": "item/flint" } ] } ] }"""));
    }

    [Theory]
    [InlineData("ruin_surface", Rarity.Common, Rarity.Uncommon)]
    [InlineData("ruin_underground", Rarity.Uncommon, Rarity.Rare)]
    [InlineData("ruin_underground_frozen", Rarity.Uncommon, Rarity.Rare)]
    [InlineData("ruin_underground_desert", Rarity.Uncommon, Rarity.Rare)]
    [InlineData("ruin_underground_crystal", Rarity.Uncommon, Rarity.Rare)]
    [InlineData("ruin_underground_fungal", Rarity.Uncommon, Rarity.Rare)]
    [InlineData("ruin_underground_magma", Rarity.Uncommon, Rarity.Rare)]
    [InlineData("ruin_hell", Rarity.Rare, Rarity.Epic)]
    [InlineData("ruin_sky", Rarity.Rare, Rarity.Legendary)]
    public void EveryRuinTableHandsOutGearWithinItsTier(string name, Rarity least, Rarity most)
    {
        var table = ContentRegistry.LootTableFor(name)!;
        var gear = 0;
        for (var seed = 0; seed < 400; seed++)
            foreach (var stack in Stacks(table, seed))
            {
                Assert.True(stack.Count >= 1);
                if (stack.Rarity is not { } rarity) continue;
                Assert.IsType<GearItem>(ItemRegistry.TryGet(stack.Item));
                Assert.InRange((int)rarity, (int)least, (int)most);
                gear++;
            }
        Assert.True(gear > 0, $"{name} never rolled any gear in 400 rolls");
    }

    [Fact]
    public void ADeeperRealmIsRicherInTheRarestTiersThanTheSurface()
    {
        static double RareShare(string name)
        {
            var table = ContentRegistry.LootTableFor(name)!;
            var rare = 0;
            var total = 0;
            for (var seed = 0; seed < 600; seed++)
                foreach (var stack in Stacks(table, seed).Where(s => s.Rarity != null))
                {
                    total++;
                    if (stack.Rarity >= Rarity.Rare) rare++;
                }
            return total == 0 ? 0 : (double)rare / total;
        }
        Assert.True(RareShare("ruin_sky") > RareShare("ruin_surface"));
        Assert.True(RareShare("ruin_hell") > RareShare("ruin_underground"));
    }
}
