using System;
using System.Collections.Generic;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// Resolves the cross-block references of <c>data/blocks</c> once every file is loaded: files load
/// alphabetically, so a block's hardening targets, freeze and melt targets, multiblock part and source, wall block, upper
/// half and <c>pickAs</c> target may not exist yet while its own file is read. <see cref="BlockLoader"/>
/// queues each block's references here as it parses it, then <see cref="Resolve"/> runs the lot.
/// </summary>
internal sealed class BlockLinkResolver
{
    private readonly List<(ushort Id, string? To, string? Touching, string Path)> _harden = [];
    private readonly List<(ushort Id, string? FreezesTo, string? MeltsTo, string Path)> _status = [];
    private readonly List<(ushort Id, string? Part, string? Source, string Path)> _multiblock = [];
    private readonly List<(ushort Id, string Wall, string Path)> _wall = [];
    private readonly List<(ushort Id, string Upper, string Path)> _upper = [];
    private readonly List<(ushort Id, string Target, string Path)> _pick = [];

    /// <summary>Queues the references one block's definition declares (contact hardening, freezesTo / meltsTo, multiblock, wall block, upper half, pickAs).</summary>
    internal void Queue(ushort id, BlockDefinition definition, string path)
    {
        var flow = definition.Flow;
        if ((flow?.HardensTo == null) != (flow?.HardensTouching == null))
            throw new InvalidOperationException(
                $"Block '{path}' sets only one of flow.hardensTo / flow.hardensTouching; hardening needs both.");
        _harden.Add((id, flow?.HardensTo, flow?.HardensTouching, path));
        if (definition.FreezesTo != null || definition.MeltsTo != null)
            _status.Add((id, definition.FreezesTo, definition.MeltsTo, path));
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
    internal void Resolve(Dictionary<ushort, BlockType> types)
    {
        // Contact targets resolve here, not inline: lava's flow.hardensTouching (water) may not
        // have its fluid flag yet when lava is read.
        foreach (var (pendingId, to, touching, pendingPath) in _harden)
        {
            if (to == null) continue;
            var type = types[pendingId];
            type.HardensToId = ResolveFluidLink(to, "flow.hardensTo", pendingPath, requireFluid: false, ownerId: pendingId);
            type.HardensTouchingId = ResolveFluidLink(touching, "flow.hardensTouching", pendingPath, requireFluid: true, ownerId: pendingId);
            BlockType.HardensToById[pendingId] = type.HardensToId;
            BlockType.HardensTouchingById[pendingId] = type.HardensTouchingId;
        }

        // Water freezes to ice and ice melts to water: each names a block that loads on the other side of it.
        foreach (var (pendingId, freezesTo, meltsTo, pendingPath) in _status)
        {
            var type = types[pendingId];
            type.FreezesToId = ResolveFluidLink(freezesTo, "freezesTo", pendingPath, requireFluid: false, ownerId: pendingId);
            type.MeltsToId = ResolveFluidLink(meltsTo, "meltsTo", pendingPath, requireFluid: true, ownerId: pendingId);
            BlockType.FreezesToById[pendingId] = type.FreezesToId;
            BlockType.MeltsToById[pendingId] = type.MeltsToId;
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

    /// <summary>
    /// Resolves a block-to-block link of a fluid transformation: with <paramref name="requireFluid"/>
    /// it must name a fluid other than its owner (<c>flow.hardensTouching</c>, <c>meltsTo</c>),
    /// otherwise a solid block (<c>flow.hardensTo</c>, <c>freezesTo</c>). Null names no link (0).
    /// </summary>
    private static ushort ResolveFluidLink(string? name, string field, string path, bool requireFluid, ushort ownerId)
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
                $"Block '{path}' names '{name}' in {field}, which is a fluid; {field} must produce a solid block."),
            _ => id
        };
    }

    /// <summary>
    /// Resolves a multiblock anchor's <c>multiblock.part</c>: the block that fills the seven
    /// non-anchor cells, which must itself use the part renderer (nothing else draws nothing).
    /// </summary>
    private static ushort ResolveMultiblockPart(string? name, string path)
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
    /// built from and reverts to, which must not itself be part of a multiblock nor unbreakable.
    /// </summary>
    private static ushort ResolveMultiblockSource(string? name, string path, ushort ownerId)
    {
        if (name == null)
            throw new InvalidOperationException($"Block '{path}' names no multiblock.source.");
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is not a registered block.");
        if (id == ownerId || BlockType.ById[id]!.Renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is itself a multiblock cell; a structure is built from a plain block.");
        if (BlockType.ById[id]!.Unbreakable)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is unbreakable; converting would overwrite it.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>wallBlock</c>: the variant a placement against a block's side becomes.
    /// It must be another block that faces horizontally and hangs off the wall behind it, and the
    /// owner itself must stand on the floor (support below, no facing of its own), since a placement
    /// on top keeps it. A wall block belongs to one owner, which is what it is picked and named as.
    /// </summary>
    private static ushort ResolveWallBlock(string name, string path, ushort ownerId)
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
    private static ushort ResolvePickAs(string name, string path, ushort ownerId)
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
    /// lower half is the item (when the pair has one, a door; a waystone has none) and the drop, so
    /// the upper half may never be held; each half belongs to one pair; and neither may topple, pop
    /// or flow away from the other.
    /// </summary>
    private static ushort ResolveUpperHalf(string name, string path, ushort ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf, which is not a registered block.");
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in upperHalf.");
        var lower = BlockType.ById[ownerId]!;
        var upper = BlockType.ById[id]!;
        if (upper.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf; the upper half must set hasItem: false (only the lower half may be held).");
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
