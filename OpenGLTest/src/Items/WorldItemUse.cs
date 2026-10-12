using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using VoxelEngine.Juice;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Items;

/// <summary>
/// The one <see cref="IItemUseWorld"/> implementation: every <see cref="Item"/> override runs its
/// effects through it instead of growing a branch in the world. <see cref="DispatchUse"/> runs an
/// item's use for a player and applies its outcome, and the active tool instance and player it
/// sets are what the interface's inventory tail reads. Owned by <see cref="World"/>; main thread
/// only.
/// </summary>
internal sealed class WorldItemUse : IItemUseWorld
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly FluidManager _fluidManager;
    private readonly LeafDecayManager _leafDecay;
    private readonly FallingBlockManager _falling;
    private readonly LodManager _lodManager;
    private readonly TntManager _tntManager;
    private readonly CampfireManager _campfires;
    private readonly KilnManager _kilns;
    private readonly PlayerHealth _health;
    private readonly HotbarPickupPop _hotbarPops;
    private readonly BlockDebrisSystem _debris;
    private readonly bool _clientMode;
    private readonly bool _serverMode;

    private double Now => _world.Now;
    private Func<float> UseRoll => _world.UseRoll;
    private IReadOnlyList<ServerPlayer> ServerPlayers => _world.ServerPlayers;
    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private BlockEditor Edits => _world.Edits;
    private BlockPlacer Placer => _world.Placer;
    private BlockInteractions Interactions => _world.Interactions;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;

    internal WorldItemUse(World world, ChunkManager chunkManager, DropManager dropManager,
        FluidManager fluidManager, LeafDecayManager leafDecay, FallingBlockManager falling,
        LodManager lodManager, TntManager tntManager, CampfireManager campfires, KilnManager kilns,
        PlayerHealth health, HotbarPickupPop hotbarPops, BlockDebrisSystem debris, bool clientMode,
        bool serverMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _fluidManager = fluidManager;
        _leafDecay = leafDecay;
        _falling = falling;
        _lodManager = lodManager;
        _tntManager = tntManager;
        _campfires = campfires;
        _kilns = kilns;
        _health = health;
        _hotbarPops = hotbarPops;
        _debris = debris;
        _clientMode = clientMode;
        _serverMode = serverMode;
    }

    // Tool instance the item use dispatcher is currently running for (0 for stackable misc
    // items like flint). Read by the IItemUseWorld tail so overrides never thread it through.
    // Main thread only, set and cleared around each OnUse call.
    private int _activeToolInstance;

    // The player that use runs for (the IItemUseWorld tail's raycasts, creative flag, tool and
    // stack spending), set and cleared with _activeToolInstance; null outside a dispatch, which
    // falls back to the local player.
    private PlayerContext? _activeContext;
    private PlayerContext ActiveContext => _activeContext ?? LocalPlayer.LocalContext();
    private Inventory ActiveInventory => ActiveContext.Inventory;

    /// <summary>Scenario seam: primes the primable block (TNT) in <paramref name="cell"/> the way a
    /// firestarter strike does, minus its chance roll, so a script can film a blast. False when the
    /// cell holds nothing primable.</summary>
    internal bool TestPrimeTnt(Vector3 cell)
    {
        var x = MathUtils.FloorToInt(cell.X);
        var y = MathUtils.FloorToInt(cell.Y);
        var z = MathUtils.FloorToInt(cell.Z);
        IItemUseWorld use = this;
        if (!_chunkManager.TryGetCell(x, y, z, out var id, out _) || !use.IsPrimedTntBlock(id))
            return false;
        var at = new Vector3(x, y, z);
        if (!use.RemoveCellForPrime(at))
            return false;
        use.SpawnPrimedTnt(at, id);
        use.ScheduleFluidAround(at);
        use.MarkLodColumnEdited(x, y, z);
        return true;
    }

    /// <summary>
    /// Runs one item's <see cref="Item.OnUse"/> for <paramref name="player"/> and applies its
    /// outcome: morph the tool instance in place (only when the use took effect) and punch.
    /// Durability, cooldowns and stack spending already happened inside the override, against the
    /// player's own inventory. Returns whether the use took effect.
    /// </summary>
    internal bool DispatchUse(Item item, float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        var instance = inventory.SelectedToolInstance;
        _activeToolInstance = instance;
        _activeContext = player;
        try
        {
            var stack = instance > 0 ? inventory.GetToolStack(instance) : null;
            var result = item.OnUse(new ItemUseContext(this, maxDistance, instance), stack);
            if (result.Applied && result.MorphToId.HasValue && instance > 0)
                inventory.MorphTool(instance, result.MorphToId.Value);
            if (result.Punch)
                LocalPlayer.PunchSwing();
            return result.Applied;
        }
        finally
        {
            _activeToolInstance = 0;
            _activeContext = null;
        }
    }

    #region IItemUseWorld
    // One implementation for every Item override: queries read the live managers, effects write
    // them, and the inventory tail runs through the dispatcher's active tool instance.

    double IItemUseWorld.NowSeconds => Now;

    bool IItemUseWorld.IsCreative => ActiveContext.IsCreative;

    float IItemUseWorld.NextUseRoll() => UseRoll();

    bool IItemUseWorld.RaycastFluidSource(float maxDistance, out Vector3 hit) =>
        ActiveContext.Raycast(maxDistance, _chunkManager, out hit, out _, includeFluids: true);

    bool IItemUseWorld.RaycastSolid(float maxDistance, out Vector3 hit, out Vector3 normal) =>
        ActiveContext.Raycast(maxDistance, _chunkManager, out hit, out normal);

    bool IItemUseWorld.TryGetCell(int x, int y, int z, out byte id, out byte state) =>
        _chunkManager.TryGetCell(x, y, z, out id, out state);

    byte IItemUseWorld.GetBlockAt(Vector3 pos) => Edits.BlockIdAt(pos);

    bool IItemUseWorld.ScoopFluidCell(Vector3 pos)
    {
        var changed = _fluidManager.SetCellImmediate(pos, 0, 0, _chunkManager);
        // Scheduled for uniformity with every other edit path; a flow itself never overwrites a
        // leaf, since leaves are not replaceable
        if (!changed) return false;
        _leafDecay.ScheduleAround(pos);
        Edits.RaiseBlockEdited(pos);
        return true;
    }

    bool IItemUseWorld.PourFluidCell(Vector3 pos, byte fluidId)
    {
        var changed = _fluidManager.SetCellImmediate(pos, fluidId, FluidState.Source, _chunkManager);
        // Scheduled for uniformity with every other edit path; a flow itself never overwrites a
        // leaf, since leaves are not replaceable
        if (!changed) return false;
        _leafDecay.ScheduleAround(pos);
        Edits.RaiseBlockEdited(pos);
        return true;
    }

    bool IItemUseWorld.PlaceBlockAtFace(Vector3 hit, Vector3 normal, byte blockId, byte state,
        out Vector3 placedCell) =>
        Placer.PlaceBlockAtFace(hit, normal, blockId, state, ActiveContext.ViewDirection, out placedCell);

    void IItemUseWorld.AfterPlaced(Vector3 pos)
    {
        // As in TryPlaceBlockFromRaycast: a client leaves the fall to the server
        if (_clientMode) return;
        _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
            (int)pos.X, (int)pos.Y, (int)pos.Z);
    }

    int IItemUseWorld.PileCountFor(byte blockId, int x, int y, int z, byte state)
    {
        var pile = BlockType.ById[blockId];
        if (pile == null)
            return 0;
        return pile.Renderer switch
        {
            BlockRenderer.BranchPile => BranchPileGenerator.CountWithState(state, pile.StackMax),
            BlockRenderer.SnowLayers => SnowShape.CountWithState(x, y, z, state, pile.StackMax),
            _ => PebbleGenerator.CountWithState(x, z, state, pile.StackMax),
        };
    }

    bool IItemUseWorld.SetPileCount(Vector3 pos, byte count)
    {
        var chunk = _chunkManager.ChunkAt((int)pos.X, (int)pos.Y, (int)pos.Z);
        var changed = chunk != null && chunk.SetStateAt(pos, count, _chunkManager);
        if (changed) Edits.RaiseBlockEdited(pos);
        return changed;
    }

    int IItemUseWorld.PileSecondaryFor(byte blockId, int x, int y, int z, byte state)
    {
        var pile = BlockType.ById[blockId];
        return pile?.Renderer != BlockRenderer.Pebbles ? 0 : PebbleGenerator.ObsidianCountWithState(x, z, state, pile.StackMax);
    }

    bool IItemUseWorld.SetPileComposition(Vector3 pos, byte blockId, int total, int secondary)
    {
        var chunk = _chunkManager.ChunkAt((int)pos.X, (int)pos.Y, (int)pos.Z);
        if (chunk == null)
            return false;
        var state = BlockType.ById[blockId]?.Renderer == BlockRenderer.Pebbles
            ? PebbleGenerator.StateFor(total, secondary)
            : (byte)total;
        var changed = chunk.SetStateAt(pos, state, _chunkManager);
        if (changed) Edits.RaiseBlockEdited(pos);
        return changed;
    }

    void IItemUseWorld.UnmarkPlayerPlaced(int x, int y, int z) =>
        _chunkManager.UnmarkPlayerPlaced(x, y, z);

    void IItemUseWorld.SpawnLoot(byte blockId, Vector3 pos) =>
        _dropManager.SpawnLoot(blockId, pos);

    bool IItemUseWorld.IsCampfireLit(int x, int y, int z) => _campfires.IsLit(x, y, z);

    bool IItemUseWorld.IgniteCampfire(int x, int y, int z) => _campfires.Ignite(x, y, z);

    bool IItemUseWorld.CanIgniteKiln(int x, int y, int z) =>
        Interactions.TryTrackKilnAt(x, y, z, out var anchor) && _kilns.CanIgnite(anchor);

    bool IItemUseWorld.IgniteKiln(int x, int y, int z) =>
        Interactions.TryTrackKilnAt(x, y, z, out var anchor) && _kilns.Ignite(anchor);

    bool IItemUseWorld.TryConvertMultiblock(Vector3 hitCell, Vector3 hitNormal, byte sourceId, byte anchorId) =>
        Edits.TryConvertMultiblock(hitCell, hitNormal, sourceId, anchorId, ActiveContext.ViewDirection);

    bool IItemUseWorld.IsPrimedTntBlock(byte id) =>
        BlockType.ById[id]?.OnBreak == BlockBreakAction.PrimeTnt;

    bool IItemUseWorld.IsCellSubmerged(int x, int y, int z) =>
        TntManager.IsSubmerged(_chunkManager, x, y, z);

    bool IItemUseWorld.RemoveCellForPrime(Vector3 cell)
    {
        var removed = _chunkManager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z)?.RemoveBlockAt(cell, _chunkManager) == true;
        // Scheduled for uniformity with every other edit path
        if (!removed) return false;
        _leafDecay.ScheduleAround(cell);
        Edits.RaiseBlockEdited(cell);
        return true;
    }

    void IItemUseWorld.SpawnPrimedTnt(Vector3 cell, byte blockId)
    {
        // A client simulates no TNT: the removed cell was forwarded, and anything the server
        // makes of it arrives in its broadcast. A local fuse would blow a crater only this
        // client sees.
        if (_clientMode) return;
        _tntManager.Spawn(cell, blockId, TntManager.PlayerFuseSeconds);
    }

    void IItemUseWorld.ScheduleFluidAround(Vector3 cell) => _fluidManager.ScheduleAround(cell);

    void IItemUseWorld.MarkLodColumnEdited(int x, int y, int z) => Edits.NotifyLodEdit(x, y, z);

    bool IItemUseWorld.ToolReady() =>
        _activeToolInstance > 0 && ActiveInventory.ToolReadyAt(_activeToolInstance, Now);

    void IItemUseWorld.MarkToolUsed(float delaySeconds)
    {
        if (_activeToolInstance > 0)
            ActiveInventory.MarkToolUsed(_activeToolInstance, Now, delaySeconds);
    }

    bool IItemUseWorld.DamageTool(int amount)
    {
        if (_activeToolInstance <= 0) return false;
        var inventory = ActiveInventory;
        var itemId = inventory.ToolItemId(_activeToolInstance);
        var slot = inventory.SlotOfTool(_activeToolInstance);
        if (!inventory.DamageTool(_activeToolInstance, amount)) return false;
        ToolBroke(itemId, slot);
        return true;
    }

    bool IItemUseWorld.Heal(int amount)
    {
        // The pool belongs to the player the use runs for: this world's own in singleplayer and
        // on a client (predicted, then the server's status confirms), the sending ServerPlayer's
        // on a server, found through the dispatching context
        if (!_serverMode)
            return _health.Heal(amount);
        var context = _activeContext;
        if (context == null) return false;
        var players = ServerPlayers;
        foreach (var serverPlayer in players)
        {
            if (!ReferenceEquals(serverPlayer.Context, context)) continue;
            if (!serverPlayer.Status.Heal(amount)) return false;
            serverPlayer.StatusDirty = true;
            return true;
        }
        return false;
    }

    bool IItemUseWorld.GrowTree(Vector3 cell)
    {
        // The aimed cell names the species (its wood or leaves); the growth writer then finds
        // the tree's root in that column and grows it, or plants a sapling when there is none
        if (TreeSpeciesRegistry.ForBlock(Edits.BlockIdAt(cell)) is not { } species) return false;
        return _world.TryGrowTree(species.Name, (int)cell.X, (int)cell.Y, (int)cell.Z,
            WorldGenerationConfig.AshGrowthPulses).Succeeded;
    }

    bool IItemUseWorld.PlantTree(string species, Vector3 hit, Vector3 normal)
    {
        if (TreeSpeciesRegistry.TryGet(species) is null) return false;
        // The ground cell: the aimed cell itself when it was hit from above, else the cell
        // across the face (the writer plants on its ground when the column holds no tree)
        var ground = normal.Y > 0.5f ? hit : hit + normal;
        var result = _world.TryGrowTree(species, (int)ground.X, (int)ground.Y, (int)ground.Z, steps: 0);
        return result.Status == TreeGrowthStatus.Planted;
    }

    void IItemUseWorld.ConsumeSelected() => ActiveInventory.ConsumeSelected();
    #endregion

    // A tool that ran out of durability shatters: chips of its sprite's color burst off the hand
    // and its hotbar slot flashes (cosmetic: a server draws nothing)
    private void ToolBroke(byte itemId, int slot)
    {
        if (_serverMode) return;
        if (slot >= 0) _hotbarPops.Flash(slot);
        if (itemId == 0 || ItemRegistry.TryGet(itemId) is not { } item) return;
        var hand = Player.GetHandAnchorMatrix(Camera.Position, Camera.PlayerYaw,
            PlayerModel.FirstPersonBodyBack * (1f - Camera.ShoulderBlend), Camera.Pitch).Translation;
        _debris.Spark(hand, ToolShatter.ColorOf(item, WorldGenerationConfig.ToolShatterFallbackColor),
            0.12f, WorldGenerationConfig.ToolShatterCount, WorldGenerationConfig.ToolShatterSpeed,
            WorldGenerationConfig.ToolShatterSize);
    }
}
