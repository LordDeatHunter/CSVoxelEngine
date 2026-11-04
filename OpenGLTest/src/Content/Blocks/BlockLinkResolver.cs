using System;
using System.Collections.Generic;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// Resolves the cross-block references of <c>data/blocks</c> once every file is loaded: files load
/// alphabetically, so a block's hardening targets, multiblock part and source, wall block, upper
/// half and <c>pickAs</c> target may not exist yet while its own file is read. <see cref="BlockLoader"/>
/// queues each block's references here as it parses it, then <see cref="Resolve"/> runs the lot.
/// </summary>
internal sealed class BlockLinkResolver
{
    private readonly List<(byte Id, string? To, string? Touching, string Path)> _harden = [];
    private readonly List<(byte Id, string? Part, string? Source, string Path)> _multiblock = [];
    private readonly List<(byte Id, string Wall, string Path)> _wall = [];
    private readonly List<(byte Id, string Upper, string Path)> _upper = [];
    private readonly List<(byte Id, string Target, string Path)> _pick = [];

    /// <summary>Queues the references one block's definition declares (contact hardening, multiblock, wall block, upper half, pickAs).</summary>
    internal void Queue(byte id, BlockDefinition definition, string path)
    {
        var flow = definition.Flow;
        if ((flow?.HardensTo == null) != (flow?.HardensTouching == null))
            throw new InvalidOperationException(
                $"Block '{path}' sets only one of flow.hardensTo / flow.hardensTouching; hardening needs both.");
        _harden.Add((id, flow?.HardensTo, flow?.HardensTouching, path));
        if (definition.Multiblock != null)
            _multiblock.Add((id, definition.Multiblock.Part, definition.Multiblock.Source, path));
        if (definition.WallBlock != null)
            _wall.Add((id, definition.WallBlock, path));
        if (definition.UpperHalf != null)
            _upper.Add((id, definition.UpperHalf, path));
        if (definition.PickAs != null)
            _pick.Add((id, definition.PickAs, path));
    }

    /// <summary>Resolves every queued reference against the loaded <paramref name="types"/>, writing the target ids onto them and the per-id tables.</summary>
    internal void Resolve(Dictionary<byte, BlockType> types)
    {
        // Contact targets resolve here, not inline: lava's flow.hardensTouching (water) may not
        // have its fluid flag yet when lava is read.
        foreach (var (pendingId, to, touching, pendingPath) in _harden)
        {
            if (to == null) continue;
            var type = types[pendingId];
            type.HardensToId = ResolveHardenTarget(to, "flow.hardensTo", pendingPath, requireFluid: false, ownerId: pendingId);
            type.HardensTouchingId = ResolveHardenTarget(touching, "flow.hardensTouching", pendingPath, requireFluid: true, ownerId: pendingId);
            BlockType.HardensToById[pendingId] = type.HardensToId;
            BlockType.HardensTouchingById[pendingId] = type.HardensTouchingId;
        }

        // An anchor's part and source blocks may not exist yet while the anchor's own file is read.
        foreach (var (pendingId, part, source, pendingPath) in _multiblock)
        {
            var type = types[pendingId];
            type.MultiblockPartId = ResolveMultiblockPart(part, pendingPath);
            type.MultiblockSourceId = ResolveMultiblockSource(source, pendingPath, pendingId);
        }

        // A torch's wall block (wall_torch) loads after the torch itself.
        foreach (var (pendingId, wall, pendingPath) in _wall)
        {
            var wallId = ResolveWallBlock(wall, pendingPath, pendingId);
            types[pendingId].WallBlockId = wallId;
            types[wallId].ItemBlockId = pendingId;
        }

        // And a door's upper half may load before or after its lower half
        foreach (var (pendingId, upper, pendingPath) in _upper)
        {
            var upperId = ResolveUpperHalf(upper, pendingPath, pendingId);
            types[pendingId].UpperHalfId = upperId;
            types[upperId].LowerHalfId = pendingId;
            types[upperId].ItemBlockId = pendingId;
        }

        // A pickAs target may load after its owner too; run last, so an owner the loops above
        // already made a wall block or an upper half is refused.
        foreach (var (pendingId, target, pendingPath) in _pick)
            types[pendingId].ItemBlockId = ResolvePickAs(target, pendingPath, pendingId);
    }

    private static byte ResolveHardenTarget(string? name, string field, string path, bool requireFluid, byte ownerId)
    {
        if (name == null) return 0;

        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is not a registered block.");
        return requireFluid switch
        {
            true when !BlockType.FluidById[id] || id == ownerId => throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is not a different fluid block."),
            false when BlockType.FluidById[id] => throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is a fluid; hardening must produce a solid block."),
            _ => id
        };
    }

    /// <summary>
    /// Resolves a multiblock anchor's <c>multiblock.part</c>: the block that fills the seven
    /// non-anchor cells, which must itself use the part renderer (nothing else draws nothing).
    /// </summary>
    private static byte ResolveMultiblockPart(string? name, string path)
    {
        if (name == null)
            throw new InvalidOperationException($"Block '{path}' names no multiblock.part.");
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.part, which is not a registered block.");
        if (BlockType.ById[id]!.Renderer != BlockRenderer.MultiblockPart)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.part, which does not use renderer \"multiblock_part\".");
        return id;
    }

    /// <summary>
    /// Resolves a multiblock anchor's <c>multiblock.source</c>: the plain block the structure is
    /// built from and reverts to, which must not itself be part of a multiblock.
    /// </summary>
    private static byte ResolveMultiblockSource(string? name, string path, byte ownerId)
    {
        if (name == null)
            throw new InvalidOperationException($"Block '{path}' names no multiblock.source.");
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is not a registered block.");
        if (id == ownerId || BlockType.ById[id]!.Renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is itself a multiblock cell; a structure is built from a plain block.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>wallBlock</c>: the variant a placement against a block's side becomes.
    /// It must be another block that faces horizontally and hangs off the wall behind it, and the
    /// owner itself must stand on the floor (support below, no facing of its own), since a placement
    /// on top keeps it. A wall block belongs to one owner, which is what it is picked and named as.
    /// </summary>
    private static byte ResolveWallBlock(string name, string path, byte ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in wallBlock, which is not a registered block.");
        var owner = BlockType.ById[ownerId]!;
        var wall = BlockType.ById[id]!;
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in wallBlock.");
        if (!owner.NeedsSupport || owner.SupportAttached || owner.SupportAbove || owner.HasHorizontalFacing)
            throw new InvalidOperationException(
                $"Block '{path}' sets wallBlock but is not a floor block (needsSupport, support below, no facing); a placement on top keeps it.");
        if (!wall.HasHorizontalFacing || !wall.SupportAttached)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in wallBlock, which needs facing: \"horizontal\" and support: \"attached\".");
        if (wall.ItemBlockId != 0)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in wallBlock, which another block already names; a wall block belongs to one floor block.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>pickAs</c>: the block a middle-click picks instead of it. It must be
    /// another block that has an item, while the owner itself has none (a worldgen-only block picked
    /// as its plain counterpart) and is not already picked as another block through a
    /// <c>wallBlock</c> or <c>upperHalf</c>.
    /// </summary>
    private static byte ResolvePickAs(string name, string path, byte ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in pickAs, which is not a registered block.");
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in pickAs.");
        var owner = BlockType.ById[ownerId]!;
        var target = BlockType.ById[id]!;
        if (!target.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in pickAs, which has no item to pick.");
        if (owner.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' sets pickAs but has an item of its own; only a block with hasItem: false is picked as another.");
        if (owner.ItemBlockId != 0)
            throw new InvalidOperationException(
                $"Block '{path}' sets pickAs but is already picked as another block through a wallBlock or upperHalf.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>upperHalf</c>: the block filling the cell above it (a door's top). The
    /// two halves share one state byte, so they must agree on <c>facing</c> and <c>open</c>; the
    /// lower half is the item and the drop, so only it may be held; each half belongs to one pair;
    /// and neither may topple, pop or flow away from the other.
    /// </summary>
    private static byte ResolveUpperHalf(string name, string path, byte ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf, which is not a registered block.");
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in upperHalf.");
        var lower = BlockType.ById[ownerId]!;
        var upper = BlockType.ById[id]!;
        if (!lower.HasItem || upper.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf; the lower half must be held and the upper half must set hasItem: false.");
        if (upper.UpperHalfId != 0 || upper.LowerHalfId != 0 || upper.ItemBlockId != 0 || lower.LowerHalfId != 0)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf, but one of the two already belongs to another block; a half belongs to one pair.");
        if (lower.HasHorizontalFacing != upper.HasHorizontalFacing || lower.Openable != upper.Openable)
            throw new InvalidOperationException(
                $"Block '{path}' and its upperHalf '{name}' disagree on facing or open; the halves share one state byte.");
        foreach (var half in new[] { lower, upper })
            if (half.Fluid || half.Falls || half.NeedsSupport || half.StackMax >= 2)
                throw new InvalidOperationException(
                    $"Block '{path}' pairs with upperHalf '{name}', but '{half.Name}' is a fluid, falls, needs support or stacks; a half must stand with its partner.");
        return id;
    }
}
