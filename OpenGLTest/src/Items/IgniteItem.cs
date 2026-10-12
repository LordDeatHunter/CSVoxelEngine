using System;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Strikes a spark at the aimed block: a dry TNT block primes, a fully built firepit lights.
/// Every attempt spends one durability and starts the item's use delay; aiming anywhere else
/// does nothing. Never morphs — ignite items wear out instead of swapping.
/// The two targets are separate virtuals so a per-item class can narrow them (a spark that
/// lights piles but never primes TNT overrides <see cref="CanPrimeTnt"/> to false).
/// </summary>
public class IgniteItem : Item
{
    internal IgniteItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    /// <summary>Whether this item may prime TNT blocks.</summary>
    protected virtual bool CanPrimeTnt => true;

    /// <summary>Whether this item may light firepit piles.</summary>
    protected virtual bool CanLightPile => true;

    /// <summary>Whether this item may light a fueled kiln.</summary>
    protected virtual bool CanLightKiln => true;

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.ToolReady())
            return ItemUseResult.Ignored;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out _))
            return ItemUseResult.Ignored;

        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!world.TryGetCell(hx, hy, hz, out var hitId, out var hitState))
            return ItemUseResult.Ignored;

        if (CanPrimeTnt && world.IsPrimedTntBlock(hitId))
        {
            world.MarkToolUsed(UseDelaySeconds);
            var primed = !world.IsCellSubmerged(hx, hy, hz)
                && world.NextUseRoll() < UseChance;
            if (primed)
            {
                var cell = new System.Numerics.Vector3(hx, hy, hz);
                if (world.RemoveCellForPrime(cell))
                {
                    world.SpawnPrimedTnt(cell, hitId);
                    world.ScheduleFluidAround(cell);
                    world.MarkLodColumnEdited(hx, hy, hz);
                }
                else
                {
                    primed = false;
                }
            }
            world.DamageTool();
            return new ItemUseResult(primed, null, primed);
        }

        // A fueled kiln lights from any of its cells; an unfueled one is not a target at all,
        // so a strike against it spends neither durability nor the use delay
        if (CanLightKiln && world.CanIgniteKiln(hx, hy, hz))
        {
            world.MarkToolUsed(UseDelaySeconds);
            var kindled = world.NextUseRoll() < UseChance && world.IgniteKiln(hx, hy, hz);
            world.DamageTool();
            return new ItemUseResult(kindled, null, kindled);
        }

        if (!CanLightPile)
            return ItemUseResult.Ignored;
        var pile = BlockType.ById[UseBlockId];
        if (pile == null || hitId != UseBlockId)
            return ItemUseResult.Ignored;
        if (world.PileCountFor(UseBlockId, hx, hy, hz, hitState) < pile.StackMax)
            return ItemUseResult.Ignored;
        if (world.IsCampfireLit(hx, hy, hz))
            return ItemUseResult.Ignored;

        world.MarkToolUsed(UseDelaySeconds);
        var lit = world.NextUseRoll() < UseChance && world.IgniteCampfire(hx, hy, hz);
        world.DamageTool();
        return new ItemUseResult(lit, null, lit);
    }
}
