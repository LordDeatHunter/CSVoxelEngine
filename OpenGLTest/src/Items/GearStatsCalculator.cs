using System;
using System.Collections.Generic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Sums the stats of worn gear (GL-free): each piece's fixed modifiers, the modifiers it rolled, and the bonuses of
/// every set whose worn piece count reaches them, clamped to what the game allows.
/// </summary>
public static class GearStatsCalculator
{
    /// <summary>The clamped stat sum of the given worn stacks (non-gear stacks add nothing).</summary>
    public static GearStats Compute(IEnumerable<ItemStack> worn)
    {
        var total = GearStats.None;
        Dictionary<string, int>? sets = null;
        foreach (var stack in worn)
        {
            if (stack.Item is not GearItem gear) continue;
            foreach (var modifier in gear.Info.Modifiers)
                total = total.With(modifier.Stat, modifier.Value);
            if (stack.Has<ModifiersComponent>())
                foreach (var modifier in stack.Get<ModifiersComponent>().Modifiers)
                    total = total.With(modifier.Stat, modifier.Value);
            if (gear.Info.SetName is { } setName && GearSlots.CountsForSets(gear.Info.Slot))
            {
                sets ??= new Dictionary<string, int>(StringComparer.Ordinal);
                sets[setName] = sets.GetValueOrDefault(setName) + 1;
            }
        }

        if (sets != null)
            foreach (var (setName, count) in sets)
            {
                if (!GearSetRegistry.TryGet(setName, out var set)) continue;
                foreach (var bonus in set.Bonuses)
                    if (count >= bonus.Pieces)
                        foreach (var modifier in bonus.Modifiers)
                            total = total.With(modifier.Stat, modifier.Value);
            }
        return total.Clamped();
    }

    /// <summary>The rarity of one gear instance: what it rolled, else its item's own.</summary>
    public static Rarity RarityOf(ItemStack stack) =>
        stack.Has<ModifiersComponent>() ? stack.Get<ModifiersComponent>().Rarity : stack.Item.Rarity;

    /// <summary>How many pieces of a set are worn (distinct pieces, counted from <paramref name="worn"/>).</summary>
    public static int WornCount(GearSet set, IEnumerable<ItemStack> worn)
    {
        var count = 0;
        foreach (var stack in worn)
            if (stack.Item is GearItem { Info.SetName: { } name } g && name == set.Name && GearSlots.CountsForSets(g.Info.Slot))
                count++;
        return count;
    }
}
