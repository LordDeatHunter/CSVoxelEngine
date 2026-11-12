using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Restores health (a bandage, cooked meat): heals the player the use runs for by the item's own
/// <see cref="Heals"/> (its <c>heals</c>, or <see cref="PlayerConfig.BandageHealAmount"/> when it declares
/// none), spending one unit from its own stack.
/// Refused at full health, so the stack is never spent for nothing. The pool is the local
/// player's in singleplayer and a client's mirrored one; on a server the Use intent heals the
/// sending player's own <see cref="PlayerHealth"/>.
/// </summary>
public class HealItem : Item
{
    internal HealItem(ItemFactory.ItemSpec spec) : base(spec)
    {
        Heals = spec.Heals;
    }

    /// <summary>The health one use restores.</summary>
    public int Heals { get; }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.Heal(Heals))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
