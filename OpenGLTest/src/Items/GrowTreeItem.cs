using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Grows the tree the targeted cell belongs to by a few pulses (ash on a trunk or a canopy),
/// spending one unit from its own stack. Refused on anything that is not a tree (or on a column
/// the growth writer cannot rebuild), so the stack is never spent for nothing.
/// </summary>
public class GrowTreeItem : Item
{
    internal GrowTreeItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out _))
            return ItemUseResult.Ignored;
        if (!world.GrowTree(new Vector3(MathF.Floor(hit.X), MathF.Floor(hit.Y), MathF.Floor(hit.Z))))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
