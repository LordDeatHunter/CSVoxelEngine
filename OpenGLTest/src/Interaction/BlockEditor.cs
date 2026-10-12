using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Juice;
using VoxelEngine.Lod;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Interaction;

/// <summary>
/// The block edit paths that take blocks away or rewrite them in place: a break with everything it
/// sets off (felling, a multiblock revert, a tall block's partner, loot, station hand-backs, the
/// support pops and topples), a multiblock conversion, the toppling trees a fell launches, and the
/// edit hooks the other paths share (the far-LOD notify and a client's prediction report), each
/// keeping the fluid, leaf-decay, gravity, LOD and canopy hooks in their order. Owned by
/// <see cref="World"/>, which keeps the toppling-tree list it fills; main thread only.
/// </summary>
internal sealed class BlockEditor
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly BlockDebrisSystem _debris;
    private readonly DropManager _dropManager;
    private readonly CampfireManager _campfires;
    private readonly QuernManager _querns;
    private readonly KilnManager _kilns;
    private readonly CastingManager _casting;
    private readonly FluidManager _fluidManager;
    private readonly LeafDecayManager _leafDecay;
    private readonly LodManager _lodManager;
    private readonly LeafParticleSystem _leaves;
    private readonly FallingBlockManager _falling;
    private readonly List<TopplingTree> _toppling;
    private readonly bool _clientMode;
    private readonly bool _serverMode;

    private bool IsPlaying => _world.IsPlaying;
    private Camera Camera => _world.Camera;
    private Action<int, int, int, byte, byte>? BlockEdited => _world.BlockEdited;

    internal BlockEditor(World world, ChunkManager chunkManager, BlockDebrisSystem debris, DropManager dropManager,
        CampfireManager campfires, QuernManager querns, KilnManager kilns, CastingManager casting,
        FluidManager fluidManager, LeafDecayManager leafDecay, LodManager lodManager, LeafParticleSystem leaves,
        FallingBlockManager falling, List<TopplingTree> toppling, bool clientMode, bool serverMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _debris = debris;
        _dropManager = dropManager;
        _campfires = campfires;
        _querns = querns;
        _kilns = kilns;
        _casting = casting;
        _fluidManager = fluidManager;
        _leafDecay = leafDecay;
        _lodManager = lodManager;
        _leaves = leaves;
        _falling = falling;
        _toppling = toppling;
        _clientMode = clientMode;
        _serverMode = serverMode;
    }

    /// <summary>
    /// Breaks the block in <paramref name="cell"/> for <paramref name="player"/> with every
    /// consequence a local break has (<see cref="RemoveBlock"/>): wood fells what it held up, any
    /// cell of a multiblock reverts the structure, loot drops unless the player is creative, a
    /// station hands back what it held, fragile blocks above pop and sand above topples, and the
    /// fluid, leaf-decay, far-LOD and canopy hooks all run. A server calls it once a player's mining
    /// was judged complete. Returns whether the cell was cleared; false for an unloaded chunk, air
    /// or a fluid (never mined), or while the world is not playing.
    /// </summary>
    internal bool BreakBlockFor(PlayerContext player, Vector3 cell)
    {
        if (!IsPlaying) return false;
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        if (!_chunkManager.TryGetCell(x, y, z, out var id, out _) || id == 0 || BlockType.IsFluid(id))
            return false;
        RemoveBlock(cell, player);
        return _chunkManager.TryGetCell(x, y, z, out var after, out _) && after == 0;
    }

    /// <summary>
    /// Block id at a world position; 0 (air) for unloaded chunks.
    /// </summary>
    internal byte BlockIdAt(Vector3 blockPos)
    {
        // blockPos components are floored floats, so the int casts are exact
        return _chunkManager.GetBlockGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);
    }

    /// <summary>
    /// Breaks the block at <paramref name="blockPos"/> for <paramref name="player"/>, with
    /// everything a break sets off. A creative player's break drops nothing (trees still fall).
    /// On a multiplayer client this is the local prediction only: no loot is rolled, no station
    /// hands anything back and nothing fells or topples, since the server runs the real break and
    /// broadcasts what it did.
    /// </summary>
    internal void RemoveBlock(Vector3 blockPos, PlayerContext player)
    {
        var chunkPos = VoxelCoords.ChunkPosOf(blockPos);

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return;

        // Read the id and state before the block goes away, so the drop knows what it is
        // (a player-built rock pile drops its own count, not the worldgen hash)
        var brokenId = BlockIdAt(blockPos);
        var brokenState = _chunkManager.GetStateGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);

        // Water is not mineable: only a bucket takes it out (and its hardness of 0 would
        // otherwise pop it on first contact)
        if (BlockType.IsFluid(brokenId)) return;

        _debris.Burst(blockPos, brokenId, WorldGenerationConfig.BlockDebrisOnBreak,
            columns: _chunkManager.ColumnSource);

        // Creative breaks drop nothing, but trees still fall. A client rolls no loot at all: what
        // a break drops is the server's, and reaches the client as drops it can pick up.
        var creative = player.IsCreative;
        var dropsLoot = !creative && !_clientMode;
        // Stations hand back what they held only where they really run
        var stationDrops = _clientMode ? null : _dropManager;

        // A mined wood block fells whatever it was holding up instead of dropping one
        // block (see FellWood): a trunk cut drops the crown, a branch cut only its children.
        // A multiplayer client fells nothing itself: it clears the cut cell like any other block
        // and forwards it, and what the server makes of the cut arrives in its broadcast.
        if (BlockType.IsTreeWood(brokenId) && !_clientMode)
            FellWood(blockPos, brokenId, brokenState, chunk, creative, player.Eye, player);
        // Mining any cell of a multiblock takes the whole structure apart instead of leaving
        // invisible part cells standing (see RevertMultiblock)
        else if (Multiblock.IsMultiblockCell(brokenId))
            RevertMultiblock(blockPos, brokenId, chunk, dropsLoot);
        // Either half of a door takes the other down with it and drops the door once (see
        // RemoveTallBlock)
        else if (TallBlock.TryGetPartner(brokenId, blockPos, out var partnerCell, out var partnerId))
            RemoveTallBlock(blockPos, brokenId, partnerCell, partnerId, chunk, dropsLoot, player);
        // Everything else rolls its loot table, but only once a block was actually cleared
        else if (chunk.RemoveBlockAt(blockPos, _chunkManager))
        {
            if (dropsLoot)
                _dropManager.SpawnLoot(brokenId, blockPos,
                    DropManager.PileCount(brokenId, (int)blockPos.X, (int)blockPos.Z, brokenState),
                    DropManager.PileSecondary(brokenId, (int)blockPos.X, (int)blockPos.Z, brokenState),
                    brokenState);
            if (BlockType.ById[brokenId]?.Renderer == BlockRenderer.BranchPile)
                _campfires.NotifyBlockRemoved((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, stationDrops);
            if (BlockType.ById[brokenId]?.Renderer == BlockRenderer.Quern)
                _querns.NotifyBlockRemoved((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, stationDrops);
            // A mined mold throws out a cast that had already set, even in creative - the mold
            // block itself is what creative withholds, not the metal someone poured into it
            if (ContentRegistry.IsCastMold(brokenId))
                _casting.NotifyBlockRemoved(blockPos, stationDrops);
        }

        // Water beside or above the hole pours into it on the next fluid tick
        _fluidManager.ScheduleAround(blockPos);
        // Leaves fed by a mined tip, and wood whose parent this was, re-derive on the next decay tick
        _leafDecay.ScheduleAround(blockPos);

        // Keep the far LOD on the current terrain: a mined top lowers the column's surface, a
        // dig below it keeps the buried shell under the newly exposed hole
        // (blockPos components are floored floats, so the int casts are exact)
        NotifyLodEdit((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);

        // Wood and leaves are never surface-solid and sit above the terrain, so the surface
        // record above never fires for them - yet the far LOD draws their canopy. Disturb it
        // and rebuild regardless. A client's far LOD is the server's (streamed through
        // LodNetCodec), so it disturbs no canopy table of its own and only bursts the leaves.
        if (TreeGenerator.IsTreeBlock(brokenId))
        {
            if (!_clientMode)
            {
                TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z, TreeGenerator.SingleEditPoisonRadius);
                _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);
            }
            _leaves.Burst(blockPos, WorldGenerationConfig.LeafParticleBurstMined);
        }

        // Pebbles, torches and their kind need the block holding them up: pop whatever the break
        // held (the block right above, a wall torch on a side). This routes through the normal
        // break path, so it rolls its loot; its own check then no-ops on air, so the recursion
        // always terminates.
        PopUnsupportedAround(blockPos, player);
        var above = blockPos + Vector3.UnitY;

        // Sand and its kind need the block under them too, but they topple into a falling
        // cube instead of popping: a no-op unless a falls block sits right above the break,
        // and its own check chains the rest of the column. A client simulates no falling
        // blocks: the server topples them and broadcasts the cells.
        if (!_clientMode)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
                (int)above.X, (int)above.Y, (int)above.Z);
        // The mined cell ends as air on every path (plain break, felling, a multiblock revert);
        // a client tags it as predicted, so the server's answer to the break is told from older
        // broadcasts
        if (BlockIdAt(blockPos) == 0)
            BlockEdited?.Invoke((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, 0, 0);
    }

    /// <summary>
    /// Breaks every support-fragile block the emptied cell <paramref name="holder"/> was holding
    /// up (see <see cref="BlockSupport"/>): the pebble or floor torch right above it and a wall
    /// torch on any side, never a wall torch that merely stood over it. Each goes through
    /// <see cref="RemoveBlock"/>, so it rolls its loot and its own dependents are checked in turn.
    /// Nothing pops while the cell still holds something solid.
    /// </summary>
    private void PopUnsupportedAround(Vector3 holder, PlayerContext player)
    {
        if (BlockType.IsSolid(BlockIdAt(holder))) return;
        for (var face = 0; face < 6; face++)
        {
            var cell = holder + Chunk.CubeFaces[face].normal;
            var id = BlockIdAt(cell);
            if (id == 0) continue;
            var state = _chunkManager.GetStateGlobal((int)cell.X, (int)cell.Y, (int)cell.Z);
            if (BlockSupport.IsHeldUpBy(id, state, cell, holder))
                RemoveBlock(cell, player);
        }
    }

    /// <summary>
    /// Fells the wood a mined wood block was holding up: collects the severed part (the break
    /// plus its children - a branch cut leaves the trunk standing), clears it in one batch
    /// (same pattern as a TNT blast), and launches a toppling entity that drops its blocks
    /// once it has lain still. Player-placed (detached) logs are not trees: they pop out as a
    /// plain drop with no entity and no animation. A lone block with no children just drops
    /// as-is. A creative break (<paramref name="creative"/>) still topples, but drops nothing.
    /// The tree tips away from <paramref name="feller"/>, the eye of whoever cut it, and anything
    /// the felled cells held up (a torch on the trunk) pops for <paramref name="player"/>.
    /// </summary>
    private void FellWood(Vector3 blockPos, byte brokenId, byte brokenState, Chunk chunk, bool creative,
        Vector3 feller, PlayerContext player)
    {
        var x = (int)blockPos.X;
        var y = (int)blockPos.Y;
        var z = (int)blockPos.Z;

        // Player-built logs never topple: no entity, no animation, just a drop.
        // RemoveBlockAt remeshes, so unlike the batch paths below nothing lingers.
        if (WoodState.IsDetached(brokenState) || _chunkManager.IsPlayerPlaced(x, y, z))
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos, state: brokenState);
            _fluidManager.ScheduleAround(blockPos);
            _leafDecay.ScheduleAround(blockPos);
            return;
        }

        var collected = TopplingTree.Collect(_chunkManager, blockPos, brokenId, brokenState);
        if (collected.Count < 2)
        {
            // Nothing was held up by this block: drop it as-is through the normal
            // single-block path, which remeshes synchronously.
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos, state: brokenState);
            _fluidManager.ScheduleAround(blockPos);
            _leafDecay.ScheduleAround(blockPos);
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        var lowestY = new Dictionary<(int X, int Z), int>();
        var min = blockPos;
        var max = blockPos;
        foreach (var block in collected)
        {
            var chunkPos = VoxelCoords.ChunkPosOf(block.Cell);
            if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var target)) continue;
            if (target.ClearBlockAt(block.Cell) == 0) continue;

            // A player-built extension felled with the tree stops counting as placed
            _chunkManager.UnmarkPlayerPlaced((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z);
            touchedChunks.Add(chunkPos);
            _fluidManager.ScheduleAround(block.Cell);
            min = Vector3.Min(min, block.Cell);
            max = Vector3.Max(max, block.Cell);

            var key = ((int)block.Cell.X, (int)block.Cell.Z);
            if (!lowestY.TryGetValue(key, out var lowest) || (int)block.Cell.Y < lowest)
                lowestY[key] = (int)block.Cell.Y;
        }

        // One box for the whole fell, never one per cell (a 500-cell fell times a reach box
        // would be hundreds of thousands of adds): the leaves left standing beyond the
        // Chebyshev-2 sweep lose their tips
        _leafDecay.ScheduleBox(min, max);

        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        // Sand stacked on the felled wood loses its support with it
        foreach (var block in collected)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
                (int)block.Cell.X, (int)block.Cell.Y + 1, (int)block.Cell.Z);

        foreach (var ((lx, lz), ly) in lowestY)
            NotifyLodEdit(lx, ly, lz);

        // The whole canopy came down above the caps: disturb it once at the break (its reach
        // covers the crown) and rebuild every touched column so no ghost lingers at range. A
        // cactus has no canopy, so it only needs the column rebuild. A client's far LOD is the
        // server's, so it touches neither the canopy table nor its regions.
        if (!_clientMode)
        {
            if (TreeGenerator.IsTreeBlock(brokenId))
                TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z, TreeGenerator.FellPoisonRadius);
            foreach (var ((lx, lz), _) in lowestY)
                _lodManager.MarkEditedColumn(lx, lz);
        }

        List<Vector3>? felledLeaves = null;
        foreach (var block in collected.Where(block => TreeGenerator.IsLeaf(block.Id)))
        {
            felledLeaves ??= [];
            felledLeaves.Add(block.Cell);
        }
        if (felledLeaves != null)
            _leaves.BurstCells(felledLeaves, WorldGenerationConfig.LeafParticleBurstFell);

        // The tree tips away from whoever felled it, pivoting on the mined cell's base
        var away = new Vector3(
            blockPos.X + 0.5f - feller.X, 0f,
            blockPos.Z + 0.5f - feller.Z);
        _toppling.Add(new TopplingTree(collected,
            new Vector3(blockPos.X + 0.5f, blockPos.Y, blockPos.Z + 0.5f), away, drops: !creative));

        // A torch on the trunk, or a flower on a felled log, loses its support with the wood
        // (RemoveBlock's own pop only looks around the cut cell)
        foreach (var block in collected)
            PopUnsupportedAround(block.Cell, player);
    }

    /// <summary>
    /// Converts the 2x2x2 volume of <paramref name="sourceId"/> that the clicked face belongs
    /// to into the <paramref name="anchorId"/> multiblock, opening toward the player
    /// (see <see cref="MultiblockPlacement.TryFindVolume"/>). The clicked cell's chunk remeshes
    /// synchronously so the click frame already shows the structure; the rest of the touched
    /// chunks rebuild asynchronously, exactly as a felled tree's do. Returns whether a volume
    /// was converted.
    /// </summary>
    internal bool TryConvertMultiblock(Vector3 hitCell, Vector3 hitNormal, byte sourceId, byte anchorId,
        Vector3 viewDirection)
    {
        if (BlockType.ById[anchorId] is not { } anchorType) return false;
        if (!MultiblockPlacement.TryFindVolume(_chunkManager.GetBlockGlobal, sourceId, hitCell,
                hitNormal, viewDirection, out var anchor, out var facing))
            return false;

        // A live falling cube owns its cell: never bury one inside the structure
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            if (_falling.OverlapsAny((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz))
                return false;
        }

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Convert(_chunkManager, anchor, facing, anchorType, touchedChunks);
        NotifyLodEditMultiblock(anchor);

        _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(hitCell), hitCell);
        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        OnMultiblockConverted(anchor, facing, anchorType);
        // A client forwards all eight cells (the anchor and its parts, each with the facing and
        // offset in its state byte), so the server holds the same structure
        RaiseBlockEditedMultiblock(anchor);
        return true;
    }

    /// <summary><see cref="RaiseBlockEdited"/> over every cell of the 2x2x2 structure at
    /// <paramref name="anchor"/>.</summary>
    private void RaiseBlockEditedMultiblock(Vector3 anchor)
    {
        if (BlockEdited == null)
            return;
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
            RaiseBlockEdited(anchor + new Vector3(dx, dy, dz));
    }

    /// <summary>
    /// Mining any cell of a multiblock takes the whole structure down: the other seven cells go
    /// back to the source block in one batch and the mined cell rolls the structure's loot when
    /// <paramref name="dropsLoot"/> (not for a creative break, and never on a client, whose break
    /// is a prediction). An orphaned cell (its anchor already blasted away) just pops on its own.
    /// </summary>
    private void RevertMultiblock(Vector3 blockPos, byte brokenId, Chunk chunk, bool dropsLoot)
    {
        if (!Multiblock.TryResolveAnchor(_chunkManager, (int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z,
                out var anchor, out _, out var anchorType) || anchorType == null)
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && dropsLoot)
                _dropManager.SpawnLoot(brokenId, blockPos);
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Revert(_chunkManager, anchor, anchorType, blockPos, touchedChunks);
        NotifyLodEditMultiblock(anchor);

        _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(blockPos), blockPos);
        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        if (dropsLoot)
            _dropManager.SpawnLoot(brokenId, blockPos);

        OnMultiblockBroken(anchor, anchorType, blockPos);
        // The other seven cells went back to the source block: a client forwards them too, or
        // the server would keep invisible part cells standing (RemoveBlock reports the mined one)
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            if (cell != blockPos)
                RaiseBlockEdited(cell);
        }
    }

    /// <summary>
    /// Mining either half of a two-cell block (a door, <see cref="TallBlock"/>): the mined cell is
    /// cleared and drops the lower half once when <paramref name="dropsLoot"/>, and the other half,
    /// if it is still there, goes through the hooks a mined cell does (fluid and leaf-decay
    /// schedules, the far LOD, the blocks it held up, a falls block above it, the client's
    /// prediction report) without dropping anything of its own.
    /// </summary>
    private void RemoveTallBlock(Vector3 blockPos, byte brokenId, Vector3 partnerCell, byte partnerId, Chunk chunk,
        bool dropsLoot, PlayerContext player)
    {
        if (!chunk.RemoveBlockAt(blockPos, _chunkManager)) return;
        if (dropsLoot)
            _dropManager.SpawnLoot(TallBlock.LowerOf(brokenId), blockPos);

        int px = (int)partnerCell.X, py = (int)partnerCell.Y, pz = (int)partnerCell.Z;
        if (!_chunkManager.TryGetCell(px, py, pz, out var id, out _) || id != partnerId) return;
        if (_chunkManager.ChunkAt(px, py, pz) is not { } partnerChunk
            || !partnerChunk.RemoveBlockAt(partnerCell, _chunkManager)) return;
        _fluidManager.ScheduleAround(partnerCell);
        _leafDecay.ScheduleAround(partnerCell);
        NotifyLodEdit(px, py, pz);
        PopUnsupportedAround(partnerCell, player);
        if (!_clientMode)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager, px, py + 1, pz);
        RaiseBlockEdited(partnerCell);
    }

    /// <summary>Runs right after a structure went up, so the managers that own its behavior
    /// can start tracking it.</summary>
    private void OnMultiblockConverted(Vector3 anchor, int facing, BlockType anchorType)
    {
        if (anchorType.Kiln)
            _kilns.Ensure(anchor, facing, anchorType.Id);
    }

    /// <summary>Runs right after a structure came down, so the managers that owned it can hand
    /// back whatever it held. <paramref name="blockPos"/> is the mined cell, which is where its
    /// contents drop.</summary>
    private void OnMultiblockBroken(Vector3 anchor, BlockType anchorType, Vector3 blockPos)
    {
        // A client's kiln is a mirror of the server's: it forgets the structure but drops nothing
        if (anchorType.Kiln)
            _kilns.NotifyBlockRemoved(anchor, blockPos, _clientMode ? null : _dropManager);
    }

    // Scratch for ThudTree: the landed blocks, split into canopy and trunk
    private readonly List<TopplingTree.FellBlock> _thudLanded = [];
    private readonly List<Vector3> _thudLeaves = [];
    private readonly List<Vector3> _thudWood = [];

    /// <summary>
    /// A felled tree just swung flat: the camera shakes by its distance to the landed trunk (harder
    /// the more wood fell, <see cref="ShakeFalloff"/>), the landed canopy bursts leaves
    /// (<see cref="LeafParticleSystem.BurstCells"/>) and the trunk kicks the ground's dust up
    /// (<see cref="GroundDust.TreeThud"/>). Cosmetic: singleplayer.
    /// </summary>
    private void ThudTree(TopplingTree tree)
    {
        _thudLanded.Clear();
        _thudLeaves.Clear();
        _thudWood.Clear();
        tree.GatherLanded(_thudLanded);
        var center = Vector3.Zero;
        foreach (var block in _thudLanded)
        {
            if (TreeGenerator.IsLeaf(block.Id))
            {
                _thudLeaves.Add(block.Cell);
                continue;
            }
            _thudWood.Add(block.Cell);
            center += block.Cell;
        }

        _leaves.BurstCells(_thudLeaves, WorldGenerationConfig.LeafParticleBurstThud);
        GroundDust.TreeThud(_thudWood, _chunkManager, _debris);
        if (_thudWood.Count == 0) return;
        center = center / _thudWood.Count + new Vector3(0.5f);
        var strength = WorldGenerationConfig.TreeThudShakeStrength
                       * MathUtils.Clamp01(_thudWood.Count / (float)WorldGenerationConfig.TreeThudFullWoodCells);
        Camera.AddShake(ShakeFalloff.Trauma(strength, WorldGenerationConfig.TreeThudShakeNearBlocks,
            WorldGenerationConfig.TreeThudShakeRangeBlocks, Vector3.Distance(Camera.Position, center)));
    }

    /// <summary>Swings falling trees, then pops their wood out as item drops once rested.</summary>
    internal void UpdateToppling(float deltaTime)
    {
        for (var i = _toppling.Count - 1; i >= 0; i--)
        {
            var tree = _toppling[i];
            tree.Update(deltaTime);
            // The moment it hits the ground: cosmetic, so a server's trees land in silence
            if (tree.TryTakeLanding() && !_serverMode)
                ThudTree(tree);
            if (!tree.IsDone) continue;

            foreach (var (id, cell) in tree.TakeDrops())
                _dropManager.Spawn(id, cell);

            tree.Dispose();
            _toppling.RemoveAt(i);
        }
    }

    /// <summary>
    /// Reports a player-driven edit of <paramref name="cell"/> through <see cref="World.BlockEdited"/>
    /// with the id and state the cell holds now, so a multiplayer client tags it as part of the
    /// prediction its next intent carries, whatever path made it. Nothing is subscribed in
    /// singleplayer or on a server.
    /// </summary>
    internal void RaiseBlockEdited(Vector3 cell)
    {
        if (BlockEdited is not { } edited)
            return;
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        edited(x, y, z, _chunkManager.GetBlockGlobal(x, y, z), _chunkManager.GetStateGlobal(x, y, z));
    }

    /// <summary>
    /// Keeps the far LOD on the current terrain after the cell (x, y, z) was edited: records the
    /// column's new surface (or its sub-surface removal cap) through
    /// <see cref="LodSurface.RecordEdit"/> and rebuilds the regions that sample the column on
    /// every level when it moved. Main thread only, called after the edit. A no-op on a client.
    /// </summary>
    internal void NotifyLodEdit(int x, int y, int z)
    {
        // A client's far LOD is the server's, streamed as finished regions through LodNetCodec:
        // LodSurface, LodRegion.ColumnCaps and TreeGenerator.CanopyDisturbed are worldgen-side
        // tables a client must never touch.
        if (_clientMode) return;
        if (LodSurface.RecordEdit(_chunkManager, x, y, z))
            _lodManager.MarkEditedColumn(x, z);
    }

    /// <summary>Runs <see cref="NotifyLodEdit"/> over every cell of the 2x2x2 structure at
    /// <paramref name="anchor"/>, after a convert or a revert rewrote them.</summary>
    private void NotifyLodEditMultiblock(Vector3 anchor)
    {
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
            NotifyLodEdit((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz);
    }
}
