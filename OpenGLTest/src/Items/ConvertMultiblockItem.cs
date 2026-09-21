using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Turns a hand-built 2x2x2 cube of <see cref="Item.UseBlockId"/> into the multiblock anchor
/// named by <see cref="Item.UseResultId"/>: the aimed cell picks the volume (see
/// <see cref="MultiblockPlacement.TryFindVolume"/>, which tries right-down, right-up, left-down
/// then left-up and points the structure's opening back at the player), and the world writes the
/// anchor plus its seven parts.
///
/// The converter is an endless tool: it spends no durability, consumes no stack and never
/// morphs, so one is enough forever.
/// </summary>
public sealed class ConvertMultiblockItem : Item
{
    internal ConvertMultiblockItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    /// <summary>
    /// The base rule reads <c>UseResultId != 0</c> as "this is a tool", but a converter's
    /// result is a <em>block</em> id, not the item it becomes. The converter is a plain endless
    /// misc item, so it stacks and carries no tool instance.
    /// </summary>
    public override InventoryCategory StorageCategory => InventoryCategory.Misc;

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;

        var cell = new Vector3((int)hit.X, (int)hit.Y, (int)hit.Z);
        return world.TryConvertMultiblock(cell, normal, UseBlockId, UseResultId)
            ? ItemUseResult.Succeed(null)
            : ItemUseResult.Ignored;
    }
}
