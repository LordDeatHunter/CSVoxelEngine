using System;
using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Plants a sapling of the item's <see cref="Item.UseTreeName"/> species on the aimed ground (an
/// acorn on dirt or grass), spending one unit from its own stack; the sapling then grows on its
/// own through <see cref="SaplingManager"/>. Refused where the species cannot root, so the stack
/// is never spent for nothing.
/// </summary>
public class PlantTreeItem : Item
{
    internal PlantTreeItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (UseTreeName is not { Length: > 0 } species)
            return ItemUseResult.Ignored;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;
        if (!world.PlantTree(species, new Vector3(MathF.Floor(hit.X), MathF.Floor(hit.Y), MathF.Floor(hit.Z)), normal))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
