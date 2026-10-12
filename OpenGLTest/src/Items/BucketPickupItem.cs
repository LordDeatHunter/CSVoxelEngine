using VoxelEngine.Content.Blocks;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Scoops a targeted fluid source into the item (empty bucket). An empty bucket scoops whatever
/// fluid it hit and becomes the full bucket whose pour targets that fluid; a fluid with no full
/// bucket cannot be taken. Always a tool instance: every scoop morphs it in place.
/// </summary>
public class BucketPickupItem : Item
{
    internal BucketPickupItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override bool TargetsFluids => true;

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastFluidSource(ctx.MaxDistance, out var hit))
            return ItemUseResult.Ignored;
        if (!world.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out var state))
            return ItemUseResult.Ignored;
        if (!BlockType.IsFluid(id) || !FluidState.IsSource(state))
            return ItemUseResult.Ignored;

        var morphTo = UseResultId;
        if (id != UseFluidId)
        {
            morphTo = 0;
            foreach (var candidate in ItemRegistry.All)
            {
                if (candidate.UseAction == ItemUseAction.PlaceFluid && candidate.UseFluidId == id)
                {
                    morphTo = candidate.Id;
                    break;
                }
            }
            if (morphTo == 0)
                return ItemUseResult.Ignored;
        }

        return world.ScoopFluidCell(hit)
            ? ItemUseResult.Succeed(morphTo)
            : ItemUseResult.Ignored;
    }
}
