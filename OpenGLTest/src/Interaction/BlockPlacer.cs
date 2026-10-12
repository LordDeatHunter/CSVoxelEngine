using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Juice;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Interaction;

/// <summary>
/// Block placement: the edit path that puts a block down against a face (the placement rules, a
/// door's upper half and the hooks every placement runs), the local and the server placement of a
/// player's selected block with its cost, the pick block, the last raycast placement a client's
/// intent names, and the pop a just-placed block draws with. Owned by <see cref="World"/>; main
/// thread only.
/// </summary>
internal sealed class BlockPlacer
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly FallingBlockManager _falling;
    private readonly QuernManager _querns;
    private readonly FluidManager _fluidManager;
    private readonly LeafDecayManager _leafDecay;
    private readonly LodManager _lodManager;
    private readonly bool _clientMode;
    private readonly bool _serverMode;

    private bool IsPlaying => _world.IsPlaying;
    private Camera Camera => _world.Camera;
    private IReadOnlyList<ServerPlayer> ServerPlayers => _world.ServerPlayers;
    private Action<int, int, int, byte, byte>? BlockEdited => _world.BlockEdited;
    private BlockEditor Edits => _world.Edits;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private LeftClickAction LeftClick => _world.LeftClick;

    internal BlockPlacer(World world, ChunkManager chunkManager, FallingBlockManager falling, QuernManager querns,
        FluidManager fluidManager, LeafDecayManager leafDecay, LodManager lodManager, bool clientMode, bool serverMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _falling = falling;
        _querns = querns;
        _fluidManager = fluidManager;
        _leafDecay = leafDecay;
        _lodManager = lodManager;
        _clientMode = clientMode;
        _serverMode = serverMode;
    }

    /// <summary>The cell the last placement went into, where its pop draws.</summary>
    internal Vector3 PlacePopPosition => _placePopPosition;

    /// <summary>The block the last placement put down, which its pop draws.</summary>
    internal byte PlacePopId => _placePopId;

    /// <summary>The hit cell the last raycast placement went against.</summary>
    internal Vector3 LastPlaceHit => _lastPlaceHit;

    /// <summary>The outward normal of the face the last raycast placement went against.</summary>
    internal Vector3 LastPlaceNormal => _lastPlaceNormal;

    // A placement pops the block in: it appears a little too big and settles into its cell
    // with one small overshoot (PlacePop), so a placement reads differently from a mining hit
    private const float PlacePopSeconds = 0.22f;
    private const float PlacePopGrow = 0.1f;

    private float _placePopElapsed = PlacePopSeconds;
    private Vector3 _placePopPosition;
    private byte _placePopId;

    // The hit cell and face the last raycast placement went against, which a client's placement
    // intent names so the server places against the same face
    private Vector3 _lastPlaceHit;
    private Vector3 _lastPlaceNormal;

    // Growth of a just-placed block's pop: it starts a little too big and settles with one small
    // overshoot (PlacePop), rendered only while grown (the chunk mesh holds the rest-size block)
    internal float PlacePopGrowNow =>
        PlacePop.Grow(_placePopElapsed, PlacePopSeconds, PlacePopGrow);

    /// <summary>
    /// Places <paramref name="player"/>'s selected block against the face of
    /// <paramref name="hitCell"/> with outward normal <paramref name="hitNormal"/> (a target a
    /// server has already checked the player can see), spending one unless the player is creative.
    /// The block comes from the player's own selected slot and must be owned and placeable; the
    /// cell follows the placement rules (<see cref="Placement"/>, no player body or live falling
    /// cube in the way, fragile blocks on solid ground) and the state byte is the world's own
    /// (a placed log is detached, a placed leaf persistent). Returns whether one was placed.
    /// </summary>
    internal bool TryPlaceSelectedBlockAt(PlayerContext player, Vector3 hitCell, Vector3 hitNormal)
    {
        if (!IsPlaying) return false;
        var inventory = player.Inventory;
        if (!inventory.CanPlaceSelected) return false;
        if (!PlaceAgainstFace(hitCell, hitNormal, inventory.SelectedBlockId, player)) return false;
        if (!player.IsCreative)
            inventory.ConsumeSelected();
        return true;
    }

    internal void AdvancePlacePop(double deltaTime)
    {
        // The placement pop's clock runs every frame on every path (a client predicts its own
        // placements), independent of the mining pop's hit-timed one
        _placePopElapsed += (float)deltaTime;
    }

    /// <summary>Places <paramref name="player"/>'s selected block where its raycast aims, spending
    /// one out of its inventory unless it is creative.</summary>
    internal bool TryPlaceSelectedBlock(float maxDistance, PlayerContext player)
    {
        if (!IsPlaying) return false;
        var inventory = player.Inventory;
        if (!inventory.CanPlaceSelected) return false;

        // Only spend a block if one actually went down (and never in creative)
        if (!TryPlaceBlockFromRaycast(maxDistance, inventory.SelectedBlockId, player)) return false;
        if (!player.IsCreative)
            inventory.ConsumeSelected();
        return true;
    }

    /// <summary>
    /// Picks the block under the crosshair into <paramref name="inventory"/>, reporting the picked
    /// block's id (0 when nothing is targeted), which a multiplayer client forwards as its
    /// <see cref="NetInventoryAction.PickBlock"/> intent.
    /// </summary>
    internal bool TryPickTargetedBlock(Inventory inventory, out byte pickedId)
    {
        pickedId = 0;
        if (!IsPlaying) return false;
        if (!LeftClick.HasSelectedBlock) return false;
        // A wall torch is picked as the torch it was placed from (BlockType.PickId)
        var id = BlockType.PickId(Edits.BlockIdAt(LeftClick.SelectedBlockPosition));
        if (id == 0) return false;
        pickedId = id;
        return inventory.PickBlock(id);
    }

    /// <summary>Places a block against the face <paramref name="player"/> targets, recording the
    /// hit (the face a client's placement intent names).</summary>
    private bool TryPlaceBlockFromRaycast(float maxDistance, byte blockId, PlayerContext player)
    {
        if (!IsPlaying) return false;
        if (blockId == 0) return false;

        if (!player.Raycast(maxDistance, _chunkManager, out var hitPos, out var hitNormal))
            return false;

        _lastPlaceHit = hitPos;
        _lastPlaceNormal = hitNormal;
        return PlaceAgainstFace(hitPos, hitNormal, blockId, player);
    }

    /// <summary>
    /// Places <paramref name="blockId"/> against the face of <paramref name="hitPos"/> with outward
    /// normal <paramref name="hitNormal"/> (see <see cref="PlaceBlockAtFace"/> for the rules), then
    /// lets sand placed into thin air fall. Spends nothing.
    /// </summary>
    private bool PlaceAgainstFace(Vector3 hitPos, Vector3 hitNormal, byte blockId, PlayerContext player)
    {
        if (!PlaceBlockAtFace(hitPos, hitNormal, blockId, state: 0, player.ViewDirection, out var placePos)) return false;

        LocalPlayer.PunchSwing();
        // Sand placed into thin air falls at once instead of hovering (stackable piles never
        // fall - the registry forbids the combination - so only the block path topples). A
        // client never ticks falling blocks, so it leaves the fall to the server, which topples
        // the placement it replays and broadcasts the cells.
        if (!_clientMode)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
                (int)placePos.X, (int)placePos.Y, (int)placePos.Z);
        return true;
    }

    /// <summary>
    /// Whether a solid block in <paramref name="cell"/> would trap a player: on a server every
    /// connected player's body counts (the server's own camera is nobody), elsewhere the local
    /// player's.
    /// </summary>
    private bool BodyBlocksPlacement(Vector3 cell)
    {
        if (!_serverMode)
            return Camera.OverlapsPlayer(cell);
        var players = ServerPlayers;
        for (var i = 0; i < players.Count; i++)
            if (PlayerContext.BodyOverlapsCell(players[i].Position, cell))
                return true;
        return false;
    }

    /// <summary>
    /// Guards and grounds one block against a raycast hit face: live cubes and a player's
    /// body refuse solid entries, fragile blocks need solid ground, and a placement into
    /// water displaces it. The filled cell is the neighbor across the hit face, or the hit
    /// cell itself when it holds a stack a placement may take over (layered snow), which it
    /// overwrites in place. <paramref name="state"/> rides into the state byte (a fresh rock
    /// pile starts at 1) and <paramref name="placedCell"/> reports the cell that was filled.
    /// <paramref name="viewDirection"/> is the placing player's look, which faces a block that
    /// takes its facing from it (stairs, <see cref="Placement.LookFacing"/>).
    /// Returns whether one was placed, without spending anything.
    /// </summary>
    internal bool PlaceBlockAtFace(Vector3 hitPos, Vector3 hitNormal, byte blockId, byte state,
        Vector3 viewDirection, out Vector3 placedCell)
    {
        placedCell = Placement.TargetCell(hitPos, hitNormal, Edits.BlockIdAt(hitPos),
            _chunkManager.GetStateGlobal((int)hitPos.X, (int)hitPos.Y, (int)hitPos.Z));
        var placePos = placedCell;
        var chunkPos = VoxelCoords.ChunkPosOf(placePos);

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return false;

        // A torch against a block's side becomes its wall torch, leaning out from that wall (the
        // facing rides in the state byte); under a ceiling it has nothing to hang from
        if (!Placement.TryMount(blockId, state, hitNormal, placePos == hitPos, viewDirection, out blockId, out state))
            return false;

        // A live cube owns its cell until it lands: placing into one would cement the cube
        // inside the new block, so the placement fails and the caller keeps its item
        if (_falling.OverlapsAny((int)placePos.X, (int)placePos.Y, (int)placePos.Z))
            return false;

        // A solid block inside a player's body would trap them (or force the physics to
        // shove them out); non-solid blocks are fine to stand in. Flying is noclip but the
        // placement is still refused, so dropping out of flight never leaves the player buried.
        if (BlockType.IsSolid(blockId) && BodyBlocksPlacement(placePos))
            return false;
        // Fragile blocks need something solid holding them up (the ground, or the wall a wall
        // torch hangs on): a flower planted into mid-air would hover until anything remeshed it,
        // so the placement fails and the caller keeps its item
        if (BlockSupport.TryGetSupportCell(blockId, state, placePos, out var support)
            && !BlockType.IsSolid(Edits.BlockIdAt(support)))
            return false;

        // A placed log is building material, not part of a tree: it is a plain full cube that
        // never connects to tree wood around it and never fells (see TreeWoodShape.Connects)
        if (BlockType.IsTreeWood(blockId) && state == 0) state = WoodState.Detached;
        // A placed leaf is decoration, not canopy: persistent, so no missing tip ever decays it
        if (TreeGenerator.IsLeaf(blockId) && state == 0) state = LeafState.Encode(0, persistent: true);

        // A door needs the cell above free for its upper half too, or nothing is placed or spent
        var upperId = BlockType.ById[blockId]?.UpperHalfId ?? 0;
        if (upperId != 0 && !CanPlaceUpperHalf(placePos + Vector3.UnitY, upperId)) return false;

        if (!chunk.PlaceBlockAt(placePos, blockId, _chunkManager, state)) return false;

        // A full solid cube can raise or re-tile the far LOD's surface. Anything else never
        // counts as that surface (LodSurface.IsSurfaceSolid), and recording it would only read as
        // a sub-surface removal and cap the column, so a torch in a cave never dents the LOD.
        if (LodSurface.IsSurfaceSolid(blockId))
            Edits.NotifyLodEdit((int)placePos.X, (int)placePos.Y, (int)placePos.Z);

        // The chunk only meshes a quern's base: register the cell so its plate draws at
        // rest until it is inserted into or spun.
        if (BlockType.ById[blockId]?.Renderer == BlockRenderer.Quern)
            _querns.Ensure((int)placePos.X, (int)placePos.Y, (int)placePos.Z);

        // A block placed into water displaces it, so the neighbors recompute their levels
        _fluidManager.ScheduleAround(placePos);
        _leafDecay.ScheduleAround(placePos);
        BlockEdited?.Invoke((int)placePos.X, (int)placePos.Y, (int)placePos.Z, blockId, state);
        if (upperId != 0)
            PlaceUpperHalf(placePos + Vector3.UnitY, upperId, state);

        // The placement pop: the block appears a little too big and settles into its cell
        // (rendered in the select pass); the upper half of a door rides along with its lower
        _placePopElapsed = 0f;
        _placePopPosition = placePos;
        _placePopId = blockId;
        return true;
    }

    /// <summary>Whether a door's upper half <paramref name="upperId"/> may go into
    /// <paramref name="cell"/>: loaded, holding nothing a placement may not take over, no live
    /// falling cube in it and no player's body in the way.</summary>
    private bool CanPlaceUpperHalf(Vector3 cell, byte upperId)
    {
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        if (!_chunkManager.TryGetCell(x, y, z, out var id, out var state) || !Placement.CanOccupy(id, state))
            return false;
        if (_falling.OverlapsAny(x, y, z)) return false;
        return !(BlockType.IsSolid(upperId) && BodyBlocksPlacement(cell));
    }

    /// <summary>Writes a door's upper half over its just-placed lower half, with the same state
    /// byte, and runs the placement hooks for it.</summary>
    private void PlaceUpperHalf(Vector3 cell, byte upperId, byte state)
    {
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        if (_chunkManager.ChunkAt(x, y, z) is not { } chunk || !chunk.PlaceBlockAt(cell, upperId, _chunkManager, state))
            return;
        _fluidManager.ScheduleAround(cell);
        _leafDecay.ScheduleAround(cell);
        BlockEdited?.Invoke(x, y, z, upperId, state);
    }
}
