using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content;
using VoxelEngine.Content.Spells;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Data;
using VoxelEngine.Rendering.Overlays;
using VoxelEngine.UI.InventoryPanel;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The hover tooltip's lines, GL-free: the display name, what the thing is (a block, a tool, a
/// plain pickup) and whichever of the item's own numbers it actually has — tool power and hit
/// pace, block and entity damage, durability left, fuel seconds, a crucible's capacity and metals, a staff's tier and bound spells, the spell parts a scroll teaches (which is
/// how the hint scroll shows its example combo). A slice of
/// the UI work (U-1); the panel that draws these lives in <see cref="HudRenderer.DrawTooltip"/>.
/// </summary>
public static class ItemTooltip
{
    /// <summary>The lines for an id, with the stack when one is hovered (its durability left).</summary>
    public static List<string> Lines(byte id, ItemStack? stack) => Lines(id, stack, null, out _);

    /// <summary>
    /// The lines for an id, and the colors of the lines that are not plain: <paramref name="colors"/> maps a line index
    /// to its color (the title takes its rarity's, a set's active bonus is green and an inactive one dim); a line not in
    /// it is drawn in the default. <paramref name="worn"/> is the wearer's equipment, which set progress counts.
    /// </summary>
    public static List<string> Lines(byte id, ItemStack? stack, Equipment? worn, out Dictionary<int, Rgba> colors)
    {
        colors = new Dictionary<int, Rgba>();
        var lines = new List<string> { stack?.ResolvedName ?? Inventory.DisplayName(id) };

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
        if (item.CrucibleCapacityMl > 0)
            AddCrucibleLines(lines, item.CrucibleCapacityMl, stack?.Get<CrucibleComponent>() ?? default);
        if (item is StaffItem staff)
            lines.Add($"Staff tier {staff.Tier}   {stack?.Get<StaffComponent>().BoundCount ?? 0}/{staff.Slots} spells");
        if (item is LearnPartsItem scroll && scroll.Teaches.Count > 0)
            lines.Add("Teaches: " + string.Join(" + ",
                scroll.Teaches.Select(k => SpellPartRegistry.TryGetByKey(k)?.DisplayName ?? k)));
        if (item is RestoreManaItem draught)
            lines.Add($"Restores {draught.Restores:P0} mana");
        if (item is GearItem gear)
            AddGearLines(lines, colors, gear, stack, worn);
        return lines;
    }

    /// <summary>
    /// The rarity and slot (replacing the plain "Item" line), what the piece changes (fixed, then rolled), the
    /// effects it blocks, its passives and its set's progress with each bonus.
    /// </summary>
    private static void AddGearLines(List<string> lines, Dictionary<int, Rgba> colors, GearItem gear, ItemStack? stack,
        Equipment? worn)
    {
        var rarity = stack != null ? GearStatsCalculator.RarityOf(stack) : gear.Rarity;
        colors[0] = Rarities.Color(rarity);
        lines[1] = $"{Rarities.Label(rarity)} {GearSlots.Label(gear.Info.Slot)}";
        foreach (var modifier in gear.Info.Modifiers)
            lines.Add(GearText.Line(modifier));
        if (stack != null && stack.Has<ModifiersComponent>())
            foreach (var modifier in stack.Get<ModifiersComponent>().Modifiers)
            {
                colors[lines.Count] = Rarities.Color(rarity);
                lines.Add(GearText.Line(modifier));
            }
        if (gear.Info.Immune.Count > 0)
            lines.Add("Immune: " + string.Join(", ", gear.Info.Immune));
        foreach (var passive in gear.Info.Passives)
            lines.Add(GearText.Passive(passive));
        foreach (var ability in GearAbilities.All)
            if ((gear.Info.Abilities & ability) != 0)
                lines.Add(GearAbilities.Label(ability));
        if (gear.Info.SetName is { } setName && GearSetRegistry.TryGet(setName, out var set))
            AddSetLines(lines, colors, set, worn);
    }

    private static void AddSetLines(List<string> lines, Dictionary<int, Rgba> colors, GearSet set, Equipment? worn)
    {
        var count = worn == null ? 0 : GearStatsCalculator.WornCount(set, worn.Worn());
        colors[lines.Count] = Rarities.Color(Rarity.Uncommon);
        lines.Add($"{set.DisplayName} set ({count}/{set.Members.Count})");
        foreach (var bonus in set.Bonuses)
        {
            colors[lines.Count] = count >= bonus.Pieces ? Rarities.Color(Rarity.Uncommon) : TextDim;
            lines.Add($"({bonus.Pieces}) " + string.Join(", ", bonus.Modifiers.Select(GearText.Line)));
        }
    }

    /// <summary>The capacity and the vessel's fill, then one line per metal: its molten ml and
    /// what is still solid.</summary>
    private static void AddCrucibleLines(List<string> lines, int capacityMl, CrucibleComponent contents)
    {
        lines.Add(contents.IsEmpty ? $"Holds {capacityMl} ml" : $"Holds {contents.TotalMl}/{capacityMl} ml");
        foreach (var metal in contents.Metals)
        {
            var name = ContentRegistry.MoltenMaterialById(metal.MaterialId)?.DisplayName ?? $"material {metal.MaterialId}";
            lines.Add(metal.PendingMl > 0
                ? $"{name} {metal.MoltenMl} ml + {metal.PendingMl} ml unmelted"
                : $"{name} {metal.MoltenMl} ml");
        }
    }
}
