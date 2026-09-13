namespace silkgltriangle;

/// <summary>
/// The block half of the unification: every placeable block is also an <see cref="Item"/> so the
/// hotbar, drops and recipes resolve blocks and pure items through one
/// <see cref="ItemRegistry"/>. Right-click places it against the aimed face; sand placed into
/// thin air still topples via <see cref="IItemUseWorld.AfterPlaced"/>. Never a tool, never a
/// fluid target. Rendering still branches on <c>BlockType.ById</c> first (cubes vs sprites), so
/// <see cref="ItemRegistry.IsItem"/> keeps meaning "pure item".
/// </summary>
public sealed class BlockItem : Item
{
    /// <summary>The block this item places.</summary>
    public BlockType Block { get; }

    internal BlockItem(BlockType block) : base(block.Id, block.DisplayName)
    {
        Block = block;
    }

    public override InventoryCategory StorageCategory => InventoryCategory.Blocks;

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;
        var placePos = hit + normal;
        if (!world.PlaceBlockAtFace(hit, normal, Id, state: 0))
            return ItemUseResult.Ignored;
        world.AfterPlaced(placePos);
        if (!world.IsCreative)
            world.ConsumeSelected();
        return ItemUseResult.Succeed(null);
    }
}
