using System;
using System.Numerics;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Wakes the boss named by <see cref="Item.UseEntityName"/> at the aimed cell (the dragonfly wing's sandworm),
/// spending one unit from its own stack (creative uses it without spending it). Refused, and not spent, where
/// the boss cannot be summoned (the aimed cell is not desert sand, or one is alive already). On a server the
/// Use intent summons for the sending player; a client only predicts the same rule.
/// </summary>
public class SummonItem : Item
{
    internal SummonItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (UseEntityName is not { Length: > 0 } entity)
            return ItemUseResult.Ignored;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out _))
            return ItemUseResult.Ignored;
        if (!world.SummonBoss(entity, new Vector3(MathF.Floor(hit.X), MathF.Floor(hit.Y), MathF.Floor(hit.Z))))
            return ItemUseResult.Ignored;
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
