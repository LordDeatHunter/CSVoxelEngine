namespace silkgltriangle;

/// <summary>
/// Builds up a multi-state block (flint on a pebble pile): augments the targeted pile or grounds
/// a fresh one, spending one unit from its own stack instead of morphing, so it stays a plain
/// misc pickup.
/// </summary>
public class PlaceStackItem : Item
{
    internal PlaceStackItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;

        var pile = BlockType.ById[UseBlockId];
        if (pile == null || pile.StackMax < 2)
            return ItemUseResult.Ignored;

        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (world.TryGetCell(hx, hy, hz, out var hitId, out var hitState) && hitId == UseBlockId)
        {
            var count = world.PileCountFor(UseBlockId, hx, hy, hz, hitState);
            if (count < pile.StackMax)
            {
                if (!world.SetPileCount(hit, (byte)(count + 1)))
                    return ItemUseResult.Ignored;
                if (!world.IsCreative)
                    world.ConsumeSelected();
                return ItemUseResult.Succeed(null);
            }
        }

        if (!world.PlaceBlockAtFace(hit, normal, UseBlockId, state: 1))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
