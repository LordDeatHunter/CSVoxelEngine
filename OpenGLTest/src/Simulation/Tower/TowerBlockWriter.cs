using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Clears the battle tower's worldgen cells that no gameplay path may remove: a floor's door once the floor is
/// cleared and a spent skeleton pile (BT-4), and later the tower's collapse (BT-8). It is the one sanctioned
/// writer past the <c>unbreakable</c> flag, over <see cref="Chunk.CollapseWriteAt"/>. Main thread only, and
/// built only where the world is authoritative (singleplayer and the server), never on a client, whose door and
/// pile cells arrive through the server's journal.
/// <para>
/// Every write runs the block-edit hooks (the edit-path checklist), in <see cref="Status.StatusEffectBlockWriter"/>'s
/// order:
/// <list type="bullet">
/// <item>raw writer: <see cref="Chunk.CollapseWriteAt"/> (through <see cref="Chunk.SetBlockRaw"/>), which marks
/// the chunk for the save and, on a server, journals the cell to the clients (<see cref="Chunk.CellWritten"/>);
/// the state byte is zeroed;</item>
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
/// cleared block was tree wood or leaves;</item>
/// <item>loot: none - a door opens and a spent pile crumbles, neither is mined;</item>
/// <item>multiplayer: no player predicted the write, so no <c>World.BlockEdited</c>;</item>
/// <item>unloaded chunks: every read goes through <see cref="ChunkManager.TryGetCell"/>, and an unloaded cell
/// is reported, never written.</item>
/// </list>
/// Writes are capped at <see cref="MobConfig.TowerMaxBlockWritesPerFrame"/> a frame.
/// </para>
/// </summary>
public sealed class TowerBlockWriter(ChunkManager chunks, FluidManager fluids, LeafDecayManager leafDecay,
    FallingBlockManager falling, LodManager lod, Action<int, int, int> notifyLodEdit,
    Action<int, int, int>? popUnsupported)
{
    private int _writesThisFrame;

    /// <summary>Starts a frame: the per-frame write budget is full again.</summary>
    public void BeginFrame() => _writesThisFrame = 0;

    /// <summary>
    /// Clears the cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) to air when it still holds
    /// <paramref name="expected"/>, running every edit hook. Returns <see cref="TowerWriteResult.Capped"/> once the
    /// frame's budget is spent, <see cref="TowerWriteResult.Unloaded"/> for a cell in an unloaded chunk and
    /// <see cref="TowerWriteResult.Gone"/> for a loaded cell holding anything else, writing nothing in each case.
    /// </summary>
    public TowerWriteResult TryClear(int x, int y, int z, byte expected)
    {
        if (_writesThisFrame >= MobConfig.TowerMaxBlockWritesPerFrame)
            return TowerWriteResult.Capped;
        if (!chunks.TryGetCell(x, y, z, out var id, out _))
            return TowerWriteResult.Unloaded;
        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null)
            return TowerWriteResult.Unloaded;
        if (id != expected)
            return TowerWriteResult.Gone;

        var cell = new Vector3(x, y, z);
        chunk.CollapseWriteAt(cell, BlockType.Air, 0);
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
        _writesThisFrame++;
        return TowerWriteResult.Written;
    }
}
