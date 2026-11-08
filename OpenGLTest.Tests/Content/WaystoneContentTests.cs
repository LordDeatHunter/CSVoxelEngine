using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Loot;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>The waystone blocks: worldgen-only rock that only a pickaxe breaks, harder than stone, and a broken one
/// always leaves something a player can pick up (it has no item of its own to drop).</summary>
public sealed class WaystoneContentTests
{
    public WaystoneContentTests() => TestContent.EnsureLoaded();

    private static List<BlockType> Waystones() =>
        BlockType.ById.Where(b => b is { Waystone: true }).Select(b => b!).ToList();

    [Fact]
    public void EveryWaystoneIsWorldgenOnlyRockOnlyAPickaxeBreaks()
    {
        var waystones = Waystones();
        Assert.NotEmpty(waystones);
        var stone = ContentRegistry.RequireBlock("stone");
        var bestPickaxe = ItemRegistry.All
            .Where(i => ContentRegistry.IsInTag("pickaxe", i.Id))
            .OrderByDescending(i => i.ToolPower)
            .First();
        var axe = ItemRegistry.All.First(i => ContentRegistry.IsInTag("axe", i.Id));

        foreach (var waystone in waystones)
        {
            Assert.False(waystone.HasItem, waystone.Name);
            Assert.True(waystone.Hardness > stone.Hardness, waystone.Name);
            Assert.False(Mining.CanMine(waystone.Id, null), waystone.Name);
            Assert.False(Mining.CanMine(waystone.Id, axe), waystone.Name);
            Assert.True(Mining.CanMine(waystone.Id, bestPickaxe), waystone.Name);
        }
    }

    [Fact]
    public void EveryWaystoneStandsAsTallAsTheArrivalRuleAssumesWithAnUpperHalfAsHardAndNoWaystoneOfItsOwn()
    {
        var axe = ItemRegistry.All.First(i => ContentRegistry.IsInTag("axe", i.Id));
        foreach (var waystone in Waystones())
        {
            // The traveler held on top of a waystone stands over its upper half
            var height = 1;
            for (var id = waystone.UpperHalfId; id != 0; id = BlockType.ById[id]!.UpperHalfId) height++;
            Assert.Equal(WaystoneArrival.StoneHeight, height);

            // Mining the upper half is as hard as mining the waystone, it opens the waystone below rather than being
            // one, and its own loot is nothing: breaking either half rolls the lower half's once
            var upper = BlockType.ById[waystone.UpperHalfId]!;
            Assert.False(upper.Waystone, upper.Name);
            Assert.False(upper.HasItem, upper.Name);
            Assert.Equal(waystone.Hardness, upper.Hardness);
            Assert.False(Mining.CanMine(upper.Id, axe), upper.Name);
            Assert.Equal(waystone.Id, TallBlock.LowerOf(upper.Id));
            var rolled = new List<byte>();
            DropManager.RollLoot(upper.Id, new LootContext(1, 64, -1), rolled);
            Assert.Empty(rolled);
        }
    }

    [Fact]
    public void ABrokenWaystoneDropsOnlyThingsAPlayerCanCarry()
    {
        var rolled = new List<byte>();
        foreach (var waystone in Waystones())
            for (var seed = 0; seed < 32; seed++)
            {
                rolled.Clear();
                DropManager.RollLoot(waystone.Id, new LootContext(seed, 64, -seed), rolled);
                Assert.NotEmpty(rolled);
                Assert.All(rolled, id => Assert.NotNull(Inventory.CategoryOf(id)));
            }
    }
}
