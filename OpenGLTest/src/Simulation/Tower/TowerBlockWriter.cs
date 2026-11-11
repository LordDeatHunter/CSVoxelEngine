using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Lod;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Writes over the battle tower's worldgen cells that no gameplay path may remove: it clears a floor's door once the
/// floor is cleared and a spent skeleton pile (BT-4), and replaces a conquered tower's cells with their ruin (BT-8's
/// collapse, <see cref="TryReplace"/>). It is the one sanctioned writer past the <c>unbreakable</c> flag, over <see cref="Chunk.CollapseWriteAt"/>. Main thread only, and
/// built only where the world is authoritative (singleplayer and the server), never on a client, whose door and
/// pile cells arrive through the server's journal.
/// <para>
/// Every write runs the block-edit hooks (the edit-path checklist), in <see cref="Status.StatusEffectBlockWriter"/>'s
/// order:
/// <list type="bullet">
/// <item>raw writer: <see cref="Chunk.CollapseWriteAt"/> (through <see cref="Chunk.SetBlockRaw"/>), which marks
/// the chunk for the save and, on a server, journals the cell to the clients (<see cref="Chunk.CellWritten"/>);
/// the state byte is the given one (zero for a clear);</item>
/// <item>player-placed: <see cref="ChunkManager.UnmarkPlayerPlaced"/>;</item>
/// <item>remesh: <see cref="ChunkManager.MarkAreaDirty"/> over the light radius, asynchronous, never a loop of
/// immediate remeshes;</item>
/// <item>fluids: <see cref="FluidManager.ScheduleAround(int, int, int)"/>;</item>
/// <item>leaf decay: <see cref="LeafDecayManager.ScheduleAround(int, int, int)"/>;</item>
/// <item>gravity: <see cref="FallingBlockManager.TryTopple"/> on the cell above;</item>
/// <item>far LOD: the <c>notifyLodEdit</c> hook (<c>BlockEditor.NotifyLodEdit</c>);</item>
/// <item>needs-support: the <c>popUnsupported</c> hook (<c>BlockEditor.PopUnsupportedAt</c>), which pops what
/// the emptied cell held up;</item>
/// <item>trees: <see cref="TreeGenerator.PoisonCanopy"/> and <see cref="LodManager.MarkEditedColumn"/> when the
/// replaced block was tree wood or leaves;</item>
/// <item>loot: none - a door opens and a spent pile crumbles, neither is mined - except that a replaced chest spills what it
/// holds through the <c>spillChest</c> hook (<c>ChestManager.NotifyBlockRemoved</c>), so the collapse loses nothing;</item>
/// <item>multiplayer: no player predicted the write, so no <c>World.BlockEdited</c>;</item>
/// <item>cosmetics: the <c>replaced</c> hook hears every write (cell, old block, new block), which singleplayer turns into
/// the collapse's chips, thrown blocks and rumble (<c>TowerCollapseEffects.OnCellChanged</c>; a client gets the same from
/// its journal);</item>
/// <item>unloaded chunks: every read goes through <see cref="ChunkManager.TryGetCell"/>, and an unloaded cell
/// is reported, never written.</item>
/// </list>
/// Writes are capped at <see cref="MobConfig.TowerMaxBlockWritesPerFrame"/> a frame.
/// </para>
/// </summary>
public sealed class TowerBlockWriter(ChunkManager chunks, FluidManager fluids, LeafDecayManager leafDecay,
    FallingBlockManager falling, LodManager lod, Action<int, int, int> notifyLodEdit,
    Action<int, int, int>? popUnsupported, Action<int, int, int, ChestProfile, byte>? spillChest = null,
    Action<int, int, int, ushort, ushort>? replaced = null)
{
    private int _writesThisFrame;

    /// <summary>Starts a frame: the per-frame write budget is full again.</summary>
    public void BeginFrame() => _writesThisFrame = 0;

    /// <summary>
    /// Clears the cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) to air when it still holds
    /// <paramref name="expected"/>: <see cref="TryReplace"/> with air.
    /// </summary>
    public TowerWriteResult TryClear(int x, int y, int z, ushort expected) => TryReplace(x, y, z, expected, BlockType.Air, 0);

    /// <summary>
    /// Replaces the cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) with <paramref name="block"/> and
    /// <paramref name="state"/> when it still holds <paramref name="expected"/>, running every edit hook (and spilling a replaced
    /// chest). Returns <see cref="TowerWriteResult.Capped"/> once the frame's budget is spent,
    /// <see cref="TowerWriteResult.Unloaded"/> for a cell in an unloaded chunk and <see cref="TowerWriteResult.Gone"/> for a
    /// loaded cell holding anything else, writing nothing in each case.
    /// </summary>
    public TowerWriteResult TryReplace(int x, int y, int z, ushort expected, ushort block, byte state)
    {
        if (_writesThisFrame >= MobConfig.TowerMaxBlockWritesPerFrame)
            return TowerWriteResult.Capped;
        if (!chunks.TryGetCell(x, y, z, out var id, out var oldState))
            return TowerWriteResult.Unloaded;
        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null)
            return TowerWriteResult.Unloaded;
        if (id != expected)
            return TowerWriteResult.Gone;

        var cell = new Vector3(x, y, z);
        chunk.CollapseWriteAt(cell, block, state);
        chunks.UnmarkPlayerPlaced(x, y, z);
        chunks.MarkAreaDirty(cell, cell + Vector3.One, LightingConfig.LightPropagationRadius);
        fluids.ScheduleAround(x, y, z);
        leafDecay.ScheduleAround(x, y, z);
        falling.TryTopple(chunks, lod, fluids, x, y + 1, z);
        notifyLodEdit(x, y, z);
        popUnsupported?.Invoke(x, y, z);
        if (TreeGenerator.IsTreeBlock(expected))
        {
            TreeGenerator.PoisonCanopy(x, z, TreeGenerator.SingleEditPoisonRadius);
            lod.MarkEditedColumn(x, z);
        }
        if (BlockType.ById[expected]?.Chest is { } profile)
            spillChest?.Invoke(x, y, z, profile, oldState);
        replaced?.Invoke(x, y, z, expected, block);
        _writesThisFrame++;
        return TowerWriteResult.Written;
    }
}
