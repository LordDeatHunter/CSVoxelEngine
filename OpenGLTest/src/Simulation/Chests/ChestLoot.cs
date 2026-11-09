using System;
using System.Collections.Generic;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// Fills a ruin or tower chest from its loot table the first time it is used, deterministically: everything is a pure
/// function of the chest's cell, its state byte (a tiered chest's tier picks the table) and the world seed, so opening it and breaking it unopened give the same contents, on the server
/// and in a replay. A piece of gear in the roll gets its modifiers from <see cref="GearRoller"/> on a seed derived from
/// the same value. GL-free.
/// </summary>
public static class ChestLoot
{
    /// <summary>The seed of the chest at a cell in a world: a hash of the three coordinates and the world seed.</summary>
    public static ulong SeedFor(int x, int y, int z, int worldSeed)
    {
        var h = SplitMix64.Mix((ulong)(uint)x * 0x9E3779B97F4A7C15UL);
        h = SplitMix64.Mix(h ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL));
        h = SplitMix64.Mix(h ^ ((ulong)(uint)z * 0x165667B19E3779F9UL));
        return SplitMix64.Mix(h ^ (ulong)(uint)worldSeed);
    }

    /// <summary>
    /// Rolls <paramref name="profile"/>'s loot table into a fresh chest state for the chest at the cell. A tiered chest
    /// rolls the table its cell <paramref name="state"/>'s tier picks (<see cref="ChestProfile.TableFor"/>); a profile
    /// with no table (an ordinary chest) is an empty state. Overflow past the chest's slots is lost.
    /// </summary>
    public static ChestState Materialize(ChestProfile profile, byte state, int x, int y, int z, int worldSeed)
    {
        var chest = new ChestState(profile.Slots);
        if (profile.TableFor(state) is not { } tableName || ContentRegistry.LootTableFor(tableName) is not { } table)
            return chest;

        var seed = SeedFor(x, y, z, worldSeed);
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
        var rolled = new List<LootStack>();
        table.RollStacks(new LootContext(x, y, z), rolled, random);

        var piece = 0UL;
        foreach (var stack in rolled)
        {
            if (!ChestTransfer.IsInstance(stack.Item))
            {
                ChestTransfer.Place(chest, stack.Item, stack.Count);
                continue;
            }
            for (var c = 0; c < stack.Count; c++)
            {
                var slot = chest.FirstEmpty();
                if (slot < 0) return chest;
                chest.Set(slot, new ChestSlot(stack.Item, 1, MintInstance(stack, seed + ++piece)));
            }
        }
        return chest;
    }

    // One tool or piece of gear from a rolled stack: gear rolls its modifiers at the rarity the table picked
    private static ItemStack MintInstance(LootStack stack, ulong seed)
    {
        var instance = new ItemStack(0, stack.Item);
        if (instance.Item is not GearItem gear) return instance;
        var rolled = GearRoller.Roll(gear, stack.Rarity ?? gear.Rarity, seed);
        instance.Set(rolled.Modifiers);
        if (rolled.Name != gear.Name) instance.Rename(rolled.Name);
        return instance;
    }
}
