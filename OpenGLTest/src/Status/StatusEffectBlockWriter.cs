using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Status;

/// <summary>
/// Writes the cells a status effect's block actions convert around an affected body
/// (<see cref="StatusEffectBlockRule"/>): a burning body burns the flammable blocks beside it away and
/// thaws ice, a frostburned one freezes the fluid sources under it. Owned by <see cref="World"/> and
/// drained by <see cref="MobDirector"/> from every body's <see cref="StatusEffectSet.TakeBlockActions"/>;
/// main thread only, and built only where the simulation is authoritative (singleplayer and the
/// server), never on a client, whose effect sets are mirrors that never tick. A second entry point,
/// <see cref="ApplyAt"/>, makes the same conversions in a cube around the cell a spell lands on (the
/// element's ignite, freeze and thaw impacts).
/// <para>
/// Every write runs the block-edit hooks (the edit-path checklist):
/// <list type="bullet">
/// <item>raw writer: <see cref="Chunk.SetBlockRaw"/>, which marks the chunk for the save and, on a
/// server, journals the cell to the clients (<see cref="Chunk.CellWritten"/>); the state byte is the
/// new block's own (0, or a fluid source), never the old one's;</item>
/// <item>remesh: <see cref="ChunkManager.MarkAreaDirty"/> over the light radius, asynchronous like the
/// leaf decay, never a loop of immediate remeshes;</item>
/// <item>fluids: <see cref="FluidManager.ScheduleAround(int, int, int)"/>, so water beside a burned or
/// thawed cell flows and the thaw's own water settles;</item>
/// <item>leaf decay: <see cref="LeafDecayManager.ScheduleAround(int, int, int)"/>;</item>
/// <item>gravity: <see cref="FallingBlockManager.TryTopple"/> on the cell above, since neither air nor
/// a fluid holds sand up;</item>
/// <item>needs-support: none to pop, because a cell that holds a support-fragile neighbor is never
/// emptied (<see cref="StatusEffectBlockRule.HoldsSomethingUp"/>);</item>
/// <item>far LOD: the <c>notifyLodEdit</c> hook (<c>BlockEditor.NotifyLodEdit</c>), plus
/// <see cref="TreeGenerator.PoisonCanopy"/> and <see cref="LodManager.MarkEditedColumn"/> for a burned
/// leaf;</item>
/// <item>stations: never flammable, freezable or meltable, so none is ever removed here;</item>
/// <item>loot: none - a burned or converted block was destroyed, not mined;</item>
/// <item>multiplayer: a client never runs this; a server's raw writes reach the clients through the
/// journal, and no player predicted them, so no <c>World.BlockEdited</c>;</item>
/// <item>unloaded chunks: every read goes through <see cref="ChunkManager.TryGetCell"/>, and an
/// unloaded cell is skipped.</item>
/// </list>
/// Writes are capped at <see cref="StatusEffectConfig.MaxBlockWritesPerPulse"/> per body pulse and
/// <see cref="StatusEffectConfig.MaxBlockWritesPerFrame"/> per frame.
/// </para>
/// </summary>
public sealed class StatusEffectBlockWriter(ChunkManager chunks, FluidManager fluids, LeafDecayManager leafDecay,
    FallingBlockManager falling, LodManager lod, Action<int, int, int> notifyLodEdit)
{
    private int _writesThisFrame;

    /// <summary>Starts a frame: the per-frame write budget is full again.</summary>
    public void BeginFrame() => _writesThisFrame = 0;

    /// <summary>
    /// Applies <paramref name="actions"/> to the cells around a body standing with its feet at
    /// <paramref name="feet"/>: the columns within <see cref="StatusEffectConfig.BlockReachBlocks"/>
    /// of the feet, from the cell under them to the one above them. Returns how many cells it wrote,
    /// stopping at either cap.
    /// </summary>
    public int Apply(Vector3 feet, StatusEffectBlockAction actions)
    {
        if (actions == StatusEffectBlockAction.None)
            return 0;

        var reach = StatusEffectConfig.BlockReachBlocks;
        var fx = MathUtils.FloorToInt(feet.X);
        var fy = MathUtils.FloorToInt(feet.Y);
        var fz = MathUtils.FloorToInt(feet.Z);
        return ConvertBox(fx - reach, fx + reach, fy - 1, fy + 1, fz - reach, fz + reach, feet.Y, actions);
    }

    /// <summary>
    /// Applies <paramref name="actions"/> where a spell lands: the cube of <paramref name="reach"/> cells
    /// around the impact cell <paramref name="cell"/> (floored) on all three axes. A freeze only sets the
    /// sources at or below the impact cell's own level, as if the impact were a body standing on top of
    /// that cell. Returns how many cells it wrote, stopping at either cap; every write runs the same
    /// hooks as <see cref="Apply"/>.
    /// </summary>
    public int ApplyAt(Vector3 cell, int reach, StatusEffectBlockAction actions)
    {
        if (actions == StatusEffectBlockAction.None)
            return 0;

        var cx = MathUtils.FloorToInt(cell.X);
        var cy = MathUtils.FloorToInt(cell.Y);
        var cz = MathUtils.FloorToInt(cell.Z);
        return ConvertBox(cx - reach, cx + reach, cy - reach, cy + reach, cz - reach, cz + reach, cy + 1, actions);
    }

    // Every cell of the box, Y outermost, under the per-pulse and per-frame caps
    private int ConvertBox(int minX, int maxX, int minY, int maxY, int minZ, int maxZ, float freezeCeiling,
        StatusEffectBlockAction actions)
    {
        var written = 0;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (written >= StatusEffectConfig.MaxBlockWritesPerPulse
                || _writesThisFrame >= StatusEffectConfig.MaxBlockWritesPerFrame)
                return written;
            if (TryConvertCell(x, y, z, freezeCeiling, actions))
            {
                written++;
                _writesThisFrame++;
            }
        }
        return written;
    }

    // One cell: read it, decide, and write it with every hook; false when nothing changed. A fluid
    // freezes only when its top is at or below freezeCeiling (the feet, or just over an impact cell)
    private bool TryConvertCell(int x, int y, int z, float freezeCeiling, StatusEffectBlockAction actions)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out var state))
            return false;
        var belowFeet = y + 1 <= freezeCeiling + 0.01f;
        if (!StatusEffectBlockRule.TryConvert(id, state, actions, belowFeet, out var newId, out var newState))
            return false;

        // Burning or thawing empties the cell of a solid; never one that holds a torch, a flower
        // or a lantern up, which would otherwise be left floating
        if (!BlockType.IsFluid(id) && StatusEffectBlockRule.HoldsSomethingUp(chunks, x, y, z))
            return false;
        // A falling cube sinking through the water owns its cell: never freeze around it
        if (BlockType.IsFluid(id) && falling.OverlapsAny(x, y, z))
            return false;

        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null)
            return false;
        chunk.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, newId, newState);
        chunks.UnmarkPlayerPlaced(x, y, z);

        var cell = new Vector3(x, y, z);
        chunks.MarkAreaDirty(cell, cell + Vector3.One, LightingConfig.LightPropagationRadius);
        fluids.ScheduleAround(x, y, z);
        leafDecay.ScheduleAround(x, y, z);
        falling.TryTopple(chunks, lod, fluids, x, y + 1, z);
        notifyLodEdit(x, y, z);
        if (TreeGenerator.IsTreeBlock(id))
        {
            TreeGenerator.PoisonCanopy(x, z, TreeGenerator.SingleEditPoisonRadius);
            lod.MarkEditedColumn(x, z);
        }
        return true;
    }
}
