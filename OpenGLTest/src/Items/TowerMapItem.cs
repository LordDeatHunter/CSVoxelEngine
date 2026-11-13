using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// A tower map: marks the nearest battle tower the player has neither marked nor conquered on their map
/// (<see cref="IItemUseWorld.MarkTower"/>), spending one unit from its own stack like a scroll (creative marks without
/// spending it). Refused, and not spent, when no such tower is in range. On a server the Use intent marks for the
/// sending player.
/// </summary>
public class TowerMapItem : Item
{
    internal TowerMapItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.MarkTower())
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
