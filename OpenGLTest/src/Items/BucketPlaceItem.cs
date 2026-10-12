using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>Pours a fluid source into the targeted cell (full bucket), becoming the empty item
/// again. A fragile block in the way pops exactly as a flow washing over it would.</summary>
public class BucketPlaceItem : Item
{
    internal BucketPlaceItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;

        var target = hit + normal;
        var existing = world.GetBlockAt(target);
        if (!BlockType.IsReplaceable(existing) && !BlockType.IsFluid(existing))
            return ItemUseResult.Ignored;

        if (existing != 0 && !BlockType.IsFluid(existing))
        {
            if (!world.IsCreative)
                world.SpawnLoot(existing, target);
            world.UnmarkPlayerPlaced((int)target.X, (int)target.Y, (int)target.Z);
        }

        return world.PourFluidCell(target, UseFluidId)
            ? ItemUseResult.Succeed(UseResultId)
            : ItemUseResult.Ignored;
    }
}
