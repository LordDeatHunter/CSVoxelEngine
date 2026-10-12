using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Interaction;

/// <summary>
/// The block right-clicks that run before an item's own use: cooking on and collecting from a lit
/// campfire, feeding and spinning a quern, tending a kiln (crucible, melt charge, fuel), pouring
/// into and collecting from a cast mold, and opening or shutting a door or trapdoor. Owned by
/// <see cref="World"/>; main thread only.
/// </summary>
internal sealed class BlockInteractions
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly CampfireManager _campfires;
    private readonly QuernManager _querns;
    private readonly KilnManager _kilns;
    private readonly CastingManager _casting;
    private readonly FluidManager _fluidManager;
    private readonly LeafDecayManager _leafDecay;

    private BlockEditor Edits => _world.Edits;

    internal BlockInteractions(World world, ChunkManager chunkManager, DropManager dropManager,
        CampfireManager campfires, QuernManager querns, KilnManager kilns, CastingManager casting,
        FluidManager fluidManager, LeafDecayManager leafDecay)
    {
        _world = world;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _campfires = campfires;
        _querns = querns;
        _kilns = kilns;
        _casting = casting;
        _fluidManager = fluidManager;
        _leafDecay = leafDecay;
    }

    internal bool TryCookOrCollect(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (BlockType.ById[hitId]?.Renderer != BlockRenderer.BranchPile) return false;
        if (!_campfires.IsLit(hx, hy, hz)) return false;

        var selected = inventory.SelectedBlockId;
        if (selected != 0 && inventory.SelectedCount > 0
            && ContentRegistry.CookRecipeForInput(selected) != null
            && _campfires.TryPlaceFood(hx, hy, hz, selected))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        var finished = _campfires.CollectFinished(hx, hy, hz);
        if (finished.Count == 0) return selected != 0 && ContentRegistry.CookRecipeForInput(selected) != null;
        foreach (var output in finished)
        {
            if (!inventory.Add(output))
                _dropManager.Spawn(output, new Vector3(hx, hy, hz));
        }
        return true;
    }

    /// <summary>
    /// Right-click on a quern: holding a crushable inserts one unit into an empty quern,
    /// anything else spins the plate through one full rotation (a quern already turning
    /// swallows the click). Returns whether a quern was aimed at.
    /// </summary>
    internal bool TryQuernInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (BlockType.ById[hitId]?.Renderer != BlockRenderer.Quern) return false;

        if (_querns.IsSpinning(hx, hy, hz)) return true;

        var selected = inventory.SelectedBlockId;
        if (selected != 0 && inventory.SelectedCount > 0
            && ContentRegistry.CrushRecipeForInput(selected) != null
            && _querns.TryInsert(hx, hy, hz, selected))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        _querns.StartSpin(hx, hy, hz);
        return true;
    }

    /// <summary>
    /// Resolves the kiln structure a cell belongs to and makes sure it is tracked, so any of its
    /// eight cells answers for the whole. False when the cell is not part of a kiln.
    /// </summary>
    internal bool TryTrackKilnAt(int x, int y, int z, out Vector3 anchor)
    {
        anchor = Vector3.Zero;
        if (!Multiblock.TryResolveAnchor(_chunkManager, x, y, z, out var resolved, out var facing, out var anchorType)
            || anchorType is not { Kiln: true })
            return false;

        _kilns.Ensure(resolved, facing, anchorType.Id);
        anchor = resolved;
        return true;
    }

    /// <summary>
    /// Right-click on a kiln: an empty hand lifts the crucible out of the hearth (contents and
    /// all), a crucible in hand stands one in, anything with a melt recipe is charged into the
    /// crucible already standing there, fuel goes into the hearth (up to
    /// <see cref="KilnManager.MaxFuel"/> units of one kind), a firestarter is passed through to
    /// its own <see cref="Item.OnUse"/> so it can strike, and a block in hand falls through to
    /// placement against the structure. Any other click on a kiln is spent. Returns whether the
    /// click belonged to the kiln.
    /// </summary>
    internal bool TryKilnInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (!Multiblock.IsMultiblockCell(hitId)) return false;
        if (!TryTrackKilnAt(hx, hy, hz, out var anchor)) return false;

        var held = inventory.HeldItem;

        switch (held)
        {
            // The firestarter lights the kiln from its own OnUse, which also spends its durability
            case { UseAction: ItemUseAction.IgniteCampfire }:
                return false;
            // An empty hand lifts the crucible out; a full tools pool puts it straight back
            case null:
            {
                if (_kilns.TryTakeCrucible(anchor, out var takenId, out var takenContents)
                    && CrucibleInventory.Add(inventory, takenId, takenContents) == null)
                    _kilns.TryPlaceCrucible(anchor, takenId, takenContents);
                return true;
            }
            // A crucible in hand stands in the hearth, carrying whatever that instance held
            case CrucibleItem crucible:
            {
                var instance = inventory.SelectedToolInstance;
                var stack = instance > 0 ? inventory.GetToolStack(instance) : null;
                if (stack != null && _kilns.TryPlaceCrucible(anchor, crucible.Id, stack.Get<CrucibleComponent>()))
                    inventory.RemoveTool(instance);
                return true;
            }
        }

        // Ore (or anything else that melts) charges the crucible standing inside
        if (ContentRegistry.MeltRecipeForInput(held.Id) != null && _kilns.TryInsertMelt(anchor, held.Id))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        if (held is not { FuelSeconds: > 0f } || !_kilns.TryAddFuel(anchor, held)) return held is not BlockItem;
        if (!player.IsCreative)
            inventory.ConsumeSelected();

        return true;
    }

    /// <summary>
    /// Right-click on a cast mold: a cooled cast is collected into the inventory (or thrown out
    /// when there is no room), otherwise a crucible in hand starts or resumes pouring its molten
    /// metal into the pocket. A crucible click is spent either way, so a refused pour never falls
    /// through to placing anything; a block in hand does fall through, so molds can still be
    /// stacked against each other. Returns whether the click belonged to the mold.
    /// </summary>
    internal bool TryCastInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (!ContentRegistry.IsCastMold(hitId)) return false;

        var cell = new Vector3(hx, hy, hz);
        var finished = _casting.Collect(cell);
        if (finished.Count > 0)
        {
            foreach (var id in finished)
                if (!inventory.Add(id))
                    _dropManager.Spawn(id, cell);
            return true;
        }

        if (inventory.HeldItem is not CrucibleItem) return false;
        var instance = inventory.SelectedToolInstance;
        var stack = instance > 0 ? inventory.GetToolStack(instance) : null;
        if (stack != null)
            // A connected player's pour drains that player's crucible and follows its eye
            _casting.TryStartPour(cell, hitId, stack, player.Camera == null ? player : null);

        return true;
    }

    /// <summary>
    /// Right-click on an openable block (a trapdoor, either half of a door;
    /// <see cref="BlockType.Openable"/>): flips its open bit (<see cref="DoorState"/>), and a
    /// door's other half with it, whatever is in hand, so a held block never places against it. The flip is a state write through <see cref="Chunk.SetStateAt"/> (saved,
    /// journaled to clients and remeshed at once) with the edit-path hooks, and a client reports
    /// it as part of its prediction. Returns whether the click belonged to an openable block.
    /// </summary>
    internal bool TryToggleOpenable(float maxDistance, PlayerContext player)
    {
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        if (!_chunkManager.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out var state)) return false;
        if (BlockType.ById[id] is not { Openable: true }) return false;
        var toggled = DoorState.Toggle(state);
        ToggleOpenCell(hit, toggled);
        // Both halves of a door share one state: the other half swings with the clicked one
        if (TallBlock.TryGetPartner(id, hit, out var partner, out var partnerId)
            && _chunkManager.TryGetCell((int)partner.X, (int)partner.Y, (int)partner.Z, out var held, out _)
            && held == partnerId)
            ToggleOpenCell(partner, toggled);
        return true;
    }

    /// <summary>Writes an openable cell's new <paramref name="state"/> and runs the hooks every
    /// edit path runs: the fluid and leaf-decay schedules and the client's prediction report.</summary>
    private void ToggleOpenCell(Vector3 cell, byte state)
    {
        var chunk = _chunkManager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z);
        if (chunk == null || !chunk.SetStateAt(cell, state, _chunkManager)) return;
        _fluidManager.ScheduleAround(cell);
        _leafDecay.ScheduleAround(cell);
        Edits.RaiseBlockEdited(cell);
    }
}
