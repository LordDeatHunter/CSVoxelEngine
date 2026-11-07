using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Interaction;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Presses a hand-built cube into the multiblock structure it is the source of: the item names no
/// structure, so every multiblock anchor is tried in id order with its own source block and size
/// (<see cref="BlockType.MultiblockSourceId"/>, <see cref="BlockType.MultiblockSize"/>), and the
/// first whose volume the aimed cell belongs to is converted - 2x2x2 mud bricks into a kiln,
/// 3x3x3 alloy bricks into an alloy furnace. The aimed cell picks the volume (see
/// <see cref="MultiblockPlacement.TryFindVolume"/>, which prefers the volume reaching farthest
/// right, then farthest down, and points the structure's opening back at the player), and the
/// world writes the anchor plus its parts.
///
/// The converter is an endless tool: it spends no durability, consumes no stack and never
/// morphs, so one is enough forever.
/// </summary>
public sealed class ConvertMultiblockItem : Item
{
    internal ConvertMultiblockItem(ItemFactory.ItemSpec spec) : base(spec)
    {
    }

    public override ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack)
    {
        var world = ctx.World;
        if (!world.RaycastSolid(ctx.MaxDistance, out var hit, out var normal))
            return ItemUseResult.Ignored;

        var cell = new Vector3((int)hit.X, (int)hit.Y, (int)hit.Z);
        foreach (var type in BlockType.ById)
        {
            if (type is not { Renderer: BlockRenderer.Multiblock }) continue;
            if (world.TryConvertMultiblock(cell, normal, type.MultiblockSourceId, type.Id))
                return ItemUseResult.Succeed(null);
        }
        return ItemUseResult.Ignored;
    }
}
