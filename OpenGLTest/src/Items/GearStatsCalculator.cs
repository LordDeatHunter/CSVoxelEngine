using System.Collections.Generic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>Sums the stats of worn gear: each piece's fixed modifiers, clamped to what the game allows (GL-free).</summary>
public static class GearStatsCalculator
{
    /// <summary>The clamped stat sum of the given worn stacks (non-gear stacks add nothing).</summary>
    public static GearStats Compute(IEnumerable<ItemStack> worn)
    {
        var total = GearStats.None;
        foreach (var stack in worn)
        {
            if (stack.Item is not GearItem gear) continue;
            foreach (var modifier in gear.Info.Modifiers)
                total = total.With(modifier.Stat, modifier.Value);
        }
        return total.Clamped();
    }
}
