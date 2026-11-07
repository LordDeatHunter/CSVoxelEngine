using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Restores mana (a mana draught): refills the item's <see cref="Restores"/> share of the pool of the
/// player the use runs for, spending one unit from its own stack (creative uses it without spending
/// it). Refused, and not spent, at a full pool. On a server the Use intent restores the sending player.
/// </summary>
public class RestoreManaItem : Item
{
    internal RestoreManaItem(ItemFactory.ItemSpec spec) : base(spec)
    {
        Restores = spec.RestoresMana;
    }

    /// <summary>The share of the mana pool one use restores, in (0, 1].</summary>
    public float Restores { get; }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RestoreMana(Restores))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
