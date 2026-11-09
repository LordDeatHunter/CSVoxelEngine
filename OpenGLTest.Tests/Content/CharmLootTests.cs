using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Loot;
using VoxelEngine.Entities;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The ability charms are loot-only: no recipe makes one, every one drops from a chest
/// or a mob table the game really rolls, and each shows in the creative catalog.
/// </summary>
public sealed class CharmLootTests
{
    public CharmLootTests() => TestContent.EnsureLoaded();

    private static readonly string[] Charms = ["feather_charm", "gill_charm", "frost_charm", "fire_charm", "magnet_charm"];

    private static IEnumerable<byte> CharmIds => Charms.Select(name => ContentRegistry.RequireItem(name).Id);

    /// <summary>Every item id a chest block's or an entity's loot table can roll, nested tables included.</summary>
    private static HashSet<byte> ReachableLoot()
    {
        var tables = new List<LootTable>();
        foreach (var block in BlockType.ById)
        {
            if (block?.Chest is not { } chest) continue;
            foreach (var name in chest.Tables)
                if (ContentRegistry.LootTableFor(name) is { } table) tables.Add(table);
        }
        foreach (var entity in EntityRegistry.ByName.Values)
            if (entity.LootTable != null) tables.Add(entity.LootTable);

        var reachable = new HashSet<byte>();
        var seen = new HashSet<LootTable>();
        var pending = new Stack<LootTable>(tables);
        while (pending.Count > 0)
        {
            var table = pending.Pop();
            if (!seen.Add(table)) continue;
            foreach (var entry in table.AllEntries())
            {
                if (entry.Nested != null) pending.Push(entry.Nested);
                else if (!entry.IsEmpty) reachable.Add(entry.Item);
            }
        }
        return reachable;
    }

    [Fact]
    public void NoCharmIsCraftable()
    {
        var crafted = ContentRegistry.CraftingRecipes.Select(r => r.ResultId).ToHashSet();
        foreach (var id in CharmIds)
            Assert.DoesNotContain(id, crafted);
    }

    [Fact]
    public void EveryCharmDropsFromAChestOrAMob()
    {
        var reachable = ReachableLoot();
        foreach (var id in CharmIds)
            Assert.Contains(id, reachable);
    }

    [Fact]
    public void EveryCharmShowsInTheCreativeCatalog()
    {
        var catalog = CreativeCatalog.Entries(InventoryCategory.Gear, Inventory.Empty()).Select(e => e.Id).ToHashSet();
        foreach (var id in CharmIds)
            Assert.Contains(id, catalog);
    }
}
