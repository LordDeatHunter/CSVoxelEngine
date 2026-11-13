using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Lays one ingot onto an ingot pile: right-clicking a pile with room adds this ingot to it (metals mix freely),
/// right-clicking anything else grounds a fresh pile holding it, spending one unit from the stack.
/// </summary>
public class PlaceIngotItem : Item
{
    internal PlaceIngotItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;

        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (world.TryGetCell(hx, hy, hz, out var hitId, out _) && hitId == UseBlockId
            && world.IngotCountAt(hx, hy, hz) < IngotPileConfig.Capacity)
        {
            if (!world.AddIngotToPile(hit, Id))
                return ItemUseResult.Ignored;
            if (!world.IsCreative)
                world.ConsumeSelected();
            return ItemUseResult.Succeed(null);
        }

        if (!world.PlaceBlockAtFace(hit, normal, UseBlockId, 0, out var placed))
            return ItemUseResult.Ignored;
        world.AddIngotToPile(placed, Id);
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
