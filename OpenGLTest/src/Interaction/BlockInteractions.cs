using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Stations;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Interaction;

/// <summary>
/// The block right-clicks that run before an item's own use: cooking on and collecting from a lit
/// campfire, feeding and spinning a quern, tending a kiln (crucible, melt charge, fuel), pouring
/// into and collecting from a cast mold, opening or shutting a door or trapdoor, and claiming a
/// spell table's click. Owned by
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
    /// cells answers for the whole. False when the cell is not part of a kiln.
    /// </summary>
    internal bool TryTrackKilnAt(int x, int y, int z, out Vector3 anchor)
    {
        anchor = Vector3.Zero;
        if (!Multiblock.TryResolveAnchor(_chunkManager, x, y, z, out var resolved, out var facing, out var anchorType)
            || anchorType is not { Kiln: not null })
            return false;

        _kilns.Ensure(resolved, facing, anchorType.Id);
        anchor = resolved;
        return true;
    }

    /// <summary>
    /// Right-click on a kiln: an empty hand lifts the crucible out of the hearth (contents and
    /// all), a crucible in hand stands one in, anything with a melt recipe is charged into the
    /// crucible already standing there, fuel goes into the hearth (up to
    /// <see cref="KilnProfile.FuelCap"/> units of one kind), a firestarter is passed through to
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
    /// Resolves the bloomery structure a cell belongs to and makes sure it is tracked, so any of its cells answers
    /// for the whole. False when the cell is not part of a bloomery.
    /// </summary>
    internal bool TryTrackBloomeryAt(int x, int y, int z, out Vector3 anchor)
    {
        anchor = Vector3.Zero;
        if (!Multiblock.TryResolveAnchor(_chunkManager, x, y, z, out var resolved, out var facing, out var anchorType)
            || anchorType is not { Bloomery: not null })
            return false;

        _world.Smithing.Bloomeries.Ensure(resolved, facing, anchorType.Id);
        anchor = resolved;
        return true;
    }

    /// <summary>
    /// Right-click on a bloomery: an empty hand collects what a finished run left (blooms, then slag; whatever does
    /// not fit the inventory drops), the profile's ore and any fuel are loaded one unit at a time, a firestarter is
    /// passed through to its own <see cref="Item.OnUse"/> so it can strike, and a block in hand falls through to
    /// placement against the structure. Any other click on a bloomery is spent. Returns whether the click belonged
    /// to the bloomery.
    /// </summary>
    internal bool TryBloomeryInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (!Multiblock.IsMultiblockCell(hitId)) return false;
        if (!TryTrackBloomeryAt(hx, hy, hz, out var anchor)) return false;

        var bloomeries = _world.Smithing.Bloomeries;
        var held = inventory.HeldItem;
        switch (held)
        {
            // The firestarter lights it from its own OnUse, which also spends its durability
            case { UseAction: ItemUseAction.IgniteCampfire }:
                return false;
            // An empty hand collects the run's output
            case null:
            {
                var output = new List<byte>();
                if (!bloomeries.TryCollect(anchor, output)) return true;
                foreach (var id in output)
                    if (!inventory.Add(id))
                        _dropManager.Spawn(id, anchor);
                return true;
            }
        }

        if (bloomeries.TryAddOre(anchor, held.Id) || (held.FuelSeconds > 0f && bloomeries.TryAddFuel(anchor, held)))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }
        return held is not BlockItem;
    }

    /// <summary>
    /// Right-click on an anvil, which holds either one bloom or a stack of one forge metal (<see cref="AnvilManager"/>):
    /// a forge metal in hand (wrought iron) stacks one more on it, the item its profile takes (a bloom) is laid on an
    /// empty anvil, and anything else (an empty hand or any other item) takes one back off, or all of the stack while
    /// sneaking. A hammer never claims the click here (it opens the recipe list, <see cref="TryOpenForge"/>, and works
    /// a bloom with the left button, <see cref="HammerAnvil"/>), and on an empty anvil the click falls through, so a
    /// block still places against it. Returns whether the click belonged to the anvil.
    /// </summary>
    internal bool TryAnvilInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        var held = inventory.HeldItem;
        if (Mining.IsHammer(held) || !TryAimedAnvil(maxDistance, player, out var cell)) return false;

        var anvils = _world.Smithing.Anvils;
        var metal = held != null && ContentRegistry.IsForgeMetal(held.Id);
        if (held != null && (metal ? anvils.TryStack(cell, held.Id) : anvils.TryPlace(cell, held.Id)))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }
        // A forge metal that does not stack (a full stack, another metal, a bloom on the anvil) takes nothing back
        if (metal) return false;
        if (!anvils.TryTake(cell, player.IsSneaking, out var taken, out var count)) return false;
        for (var i = 0; i < count; i++)
            if (!inventory.Add(taken) && !_world.IsClientMode)
                _dropManager.Spawn(taken, cell);
        return true;
    }

    /// <summary>
    /// A right-click with a smithing hammer on an anvil holding a metal stack: claims the click (it changes nothing)
    /// and names the anvil in <paramref name="cell"/>, so the local player opens the anvil's recipe list on it. A bare
    /// anvil or one holding a bloom is not claimed: the bloom is hammered with the left button.
    /// </summary>
    internal bool TryOpenForge(float maxDistance, PlayerContext player, out Vector3 cell)
    {
        cell = default;
        if (!Mining.IsHammer(player.Inventory.HeldItem) || !TryAimedAnvil(maxDistance, player, out cell)) return false;
        return _world.Smithing.Anvils.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z) is { MetalCount: > 0 };
    }

    // The anvil under this player's crosshair, tracked; false when the ray lands on anything else
    private bool TryAimedAnvil(float maxDistance, PlayerContext player, out Vector3 cell)
    {
        cell = default;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        cell = new Vector3((int)hit.X, (int)hit.Y, (int)hit.Z);
        return TryTrackAnvilAt(cell);
    }

    /// <summary>Makes sure the anvil block at <paramref name="cell"/> is tracked. False when the cell is not an
    /// anvil.</summary>
    internal bool TryTrackAnvilAt(Vector3 cell)
    {
        if (!_chunkManager.TryGetCell((int)cell.X, (int)cell.Y, (int)cell.Z, out var id, out _)
            || BlockType.ById[id]?.Anvil == null)
            return false;
        _world.Smithing.Anvils.Ensure(cell, id);
        return true;
    }

    /// <summary>
    /// One hammer hit on the anvil at <paramref name="cell"/>, if a piece lies on it and the gap since the last hit
    /// has run down (<paramref name="slackSeconds"/> of jitter allowed). A finished piece drops its result and slag on
    /// top of the anvil. Returns what the hit did; the caller supplies the hammer's own pace.
    /// </summary>
    internal AnvilHit HammerAnvil(Vector3 cell, float gapSeconds, float slackSeconds)
    {
        if (!TryTrackAnvilAt(cell)) return default;
        var hit = _world.Smithing.Anvils.TryHit(cell, gapSeconds, slackSeconds);
        if (hit.Kind != AnvilHitKind.Finished || _world.IsClientMode) return hit;

        var top = cell + new Vector3(0f, 1f, 0f);
        for (var i = 0; i < hit.ResultCount; i++)
            _dropManager.Spawn(hit.ResultId, top);
        for (var i = 0; i < hit.SlagCount; i++)
            _dropManager.Spawn(hit.SlagId, top);
        return hit;
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

    /// <summary>
    /// A right-click on a chest: claims the click (no side effect, no swing) unless the player is sneaking, which
    /// places a block against it instead. The local player opens the chest panel from the
    /// <see cref="RightClickOutcome.Container"/> outcome and asks the server (or the chest manager) for the contents; a
    /// server replaying the click claims it and does nothing. <paramref name="cell"/> is the chest's cell.
    /// </summary>
    internal bool TryOpenChest(float maxDistance, PlayerContext player, out Vector3 cell)
    {
        cell = default;
        if (player.Camera is { IsSneaking: true }) return false;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        if (!_chunkManager.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out _)) return false;
        if (BlockType.ById[id]?.Chest == null) return false;
        cell = new Vector3((int)hit.X, (int)hit.Y, (int)hit.Z);
        return true;
    }

    /// <summary>
    /// Right-click on a spell table (a block with <see cref="BlockType.SpellBuilder"/>): claims the
    /// click unless a block is in hand, which places against the table like it does against a kiln.
    /// No side effect and no block entity: the caller decides what to open, so a local player opens
    /// the spell builder client-side from the <see cref="RightClickOutcome.SpellTable"/> outcome,
    /// while a server replaying the click claims it and does nothing.
    /// </summary>
    internal bool TryOpenSpellTable(float maxDistance, PlayerContext player)
    {
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        if (!_chunkManager.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out _)) return false;
        if (BlockType.ById[id] is not { SpellBuilder: true }) return false;
        return player.Inventory.HeldItem is not BlockItem;
    }

    /// <summary>
    /// A right-click on a waystone (a block with <see cref="BlockType.Waystone"/>): claims the click (no side effect, no
    /// swing) unless the player is sneaking or holds a block, which places against it instead. The local player
    /// discovers it and opens the waystone screen from the <see cref="RightClickOutcome.Waystone"/> outcome; a server
    /// replaying the click claims it and does nothing. A click on either half of a waystone counts, and only while the
    /// whole waystone stands (<see cref="WaystoneManager.IsWaystoneAt"/>). <paramref name="cell"/> is the waystone's
    /// cell, its lower half's, which the registry keys it by.
    /// </summary>
    internal bool TryOpenWaystone(float maxDistance, PlayerContext player, out Vector3 cell)
    {
        cell = default;
        if (player.Camera is { IsSneaking: true } || player.Inventory.HeldItem is BlockItem) return false;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        if (!_chunkManager.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out _)) return false;
        if (!WaystoneManager.TryGetWaystoneCell(id, (int)hit.X, (int)hit.Y, (int)hit.Z, out var waystone)
            || !WaystoneManager.IsWaystoneAt(_chunkManager, waystone.X, waystone.Y, waystone.Z)) return false;
        cell = new Vector3(waystone.X, waystone.Y, waystone.Z);
        return true;
    }

    /// <summary>
    /// A right-click on a battle tower's roof altar (the <see cref="TowerPalette.AltarBlock"/> palette block, recognized
    /// by id like the tower's piles and doors): claims the click (no side effect, no swing) unless the player is
    /// sneaking or holds a block, which places against it instead. The caller judges the altar or begins its ritual
    /// from the <see cref="RightClickOutcome.Altar"/> outcome. <paramref name="cell"/> is the altar's cell.
    /// </summary>
    internal bool TryUseAltar(float maxDistance, PlayerContext player, out Vector3 cell)
    {
        cell = default;
        if (player.Camera is { IsSneaking: true } || player.Inventory.HeldItem is BlockItem) return false;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        if (!_chunkManager.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out _)) return false;
        if (id != TowerPalette.AltarBlock) return false;
        cell = new Vector3((int)hit.X, (int)hit.Y, (int)hit.Z);
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
