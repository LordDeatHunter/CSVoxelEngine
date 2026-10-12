using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The hover tooltip's lines, GL-free: the display name, what the thing is (a block, a tool, a
/// plain pickup) and whichever of the item's own numbers it actually has — tool power and hit
/// pace, block and entity damage, durability left, fuel seconds, a crucible's capacity. A slice of
/// the UI work (U-1); the panel that draws these lives in <see cref="Systems.UIRenderer"/>.
/// </summary>
public static class ItemTooltip
{
    /// <summary>The lines for an id, with the stack when one is hovered (its durability left).</summary>
    public static List<string> Lines(byte id, ItemStack? stack)
    {
        var lines = new List<string> { Inventory.DisplayName(id) };

        if (BlockType.ById[id] is { } block)
        {
            lines.Add("Block");
            lines.Add($"Hardness {block.Hardness:0.#}");
            if (block.LightEmission > 0) lines.Add($"Light {block.LightEmission}");
            if (block.StackMax >= 2) lines.Add($"Stacks to {block.StackMax}");
            return lines;
        }

        if (ItemRegistry.TryGet(id) is not { } item) return lines;
        lines.Add(item.IsTool ? "Tool" : "Item");
        if (item.IsTool) lines.Add($"Power {item.ToolPower:0.##}   Hit {item.HitDelayMs} ms");
        if (item.BlockDamage > Mining.DefaultBlockDamage) lines.Add($"Block damage {item.BlockDamage:0.##}");
        if (item.EntityDamage > Mining.DefaultEntityDamage) lines.Add($"Damage {item.EntityDamage}");
        if (item.MaxDurability > 0)
        {
            var left = stack?.Get<DurabilityComponent>().UsesLeft ?? item.MaxDurability;
            lines.Add($"Durability {left}/{item.MaxDurability}");
        }
        if (item.FuelSeconds > 0) lines.Add($"Fuel {item.FuelSeconds:0.#} s");
        if (item.CrucibleCapacityMl > 0) lines.Add($"Holds {item.CrucibleCapacityMl} ml");
        return lines;
    }
}
