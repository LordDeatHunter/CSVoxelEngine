using System.Collections.Generic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// A teaching scroll: teaches the spell parts in its <see cref="Teaches"/> list to the player the use
/// runs for, spending one unit from its own stack (creative learns without spending it). Refused, and
/// not spent, when every part is already known. On a server the Use intent teaches the sending player.
/// </summary>
public class LearnPartsItem : Item
{
    internal LearnPartsItem(ItemFactory.ItemSpec spec) : base(spec)
    {
        Teaches = spec.Teaches ?? [];
    }

    /// <summary>The spell part keys (<c>&lt;kind&gt;/&lt;name&gt;</c>) this scroll teaches, validated at load.</summary>
    public IReadOnlyList<string> Teaches { get; }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (Teaches.Count == 0 || !world.LearnParts(Teaches))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
