using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.Simulation.LeafDecay;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>
/// The fluid simulation: a fixed tick (<see cref="SimulationConfig.FluidTickSeconds"/>)
/// over a set of scheduled cells, mirroring Minecraft's water rules — infinite sources, a
/// flowing skirt that thins by one level per block, columns that fall, and a slope search that
/// makes water prefer the nearest hole.
///
/// Only scheduled cells do any work, and a cell reschedules its neighborhood exactly when it
/// changes or writes into a neighbor, so a settled body of water costs nothing. Every block
/// edit path in the game therefore has to call <see cref="ScheduleAround(Vector3)"/>, or the
/// water beside the edit will not notice it.
///
/// Worldgen water gets the same treatment from <see cref="WakeLoadedChunks"/>: when a chunk
/// streams in, the frontier of its water against a carved cavity is scheduled — the interior
/// found by the generation worker (<see cref="Chunk.FluidFrontier"/>), the borders scanned here
/// once both chunks are loaded — so a sea that a cave or an entrance breached pours into it
/// instead of standing there as a wall. Open air above the terrain is deliberately not woken,
/// so generated water stays static across ravines, beaches and hillsides. Once that has settled
/// it costs nothing again.
///
/// Cells are read through <see cref="FluidCells.TryGetCell"/> and never through
/// <c>GetBlockGlobal</c>: the latter reports unloaded chunks as air, which would let water
/// pour into space that has not been generated yet.
///
/// Main thread only: it writes block data and schedules remeshes.
/// </summary>
public class FluidManager
{
    private float _accumulator;

    // How many ticks have run. Fluids with a tick divisor above 1 (lava: 2) only process
    // when the index divides evenly, so they flow slower while sharing the one schedule.
    private int _tickIndex;

    private readonly FluidSchedule _schedule = new();
    private readonly FluidCells _cells = new();
    private readonly FluidNeighborhood _neighborhood;
    private readonly FluidSpread _spread;
    private readonly FluidMud _mud;
    private readonly FluidWakeScanner _wake;

    // The current tick's cells. A tick may span several frames: _tickCursor walks this list one
    // per-frame slice at a time, so a chunk-load wake storm spreads across frames instead of
    // running in one, while the simulation still keeps up with the wake.
    private readonly List<long> _tickCells = [];
    private int _tickCursor;

    // Scratch for SetCellImmediate's border neighbors (kept off the tick's set)
    private readonly HashSet<long> _borderScratch = [];

    /// <summary>
    /// False on a multiplayer client, which never ticks the fluids (the server simulates them and
    /// broadcasts the result): scheduling then returns at once, so the edit paths that still call
    /// it cannot grow a schedule nothing drains for the whole session.
    /// </summary>
    public bool Enabled { get => _schedule.Enabled; set => _schedule.Enabled = value; }

    // The cells that hardened (lava to cobblestone) during the last Update, for the steam puffs
    private readonly List<Vector3> _hardened = [];

    /// <summary>Builds the simulation's components once.</summary>
    public FluidManager()
    {
        _neighborhood = new FluidNeighborhood(_cells);
        _spread = new FluidSpread(_cells, _schedule, _neighborhood, _hardened);
        _mud = new FluidMud(_cells, _schedule);
        _wake = new FluidWakeScanner(_schedule);
    }

    /// <summary>Every cell contact hardening turned solid during the last <see cref="Update"/> (lava
    /// touching water, and a flow settling against a fluid that hardens on its touch); valid until
    /// the next one. The world puffs steam off each (cosmetic, never on a server).</summary>
    public IReadOnlyList<Vector3> HardenedCells => _hardened;

    /// <summary>Schedules a cell and its six face neighbors for the next tick. Every path that
    /// changes a block must call this, or water beside the edit will not notice it.</summary>
    public void ScheduleAround(Vector3 cell) => ScheduleAround((int)cell.X, (int)cell.Y, (int)cell.Z);

    /// <summary>Schedules a cell and its six face neighbors for the next tick.</summary>
    public void ScheduleAround(int x, int y, int z) => _schedule.ScheduleAround(x, y, z);

    /// <summary>Test seam, like <see cref="LeafDecayManager.PendingCells"/>: cells waiting for a
    /// tick, scheduled or still in the current one.</summary>
    internal int PendingCells => _schedule.Count + (_tickCells.Count - _tickCursor);

    /// <summary>
    /// Schedules every cell of the inclusive integer box for the next tick. Used by a TNT blast,
    /// which clears a whole sphere at once and needs the water around all of it to pour in.
    /// </summary>
    public void ScheduleBox(Vector3 min, Vector3 max) => _schedule.ScheduleBox(min, max);

    /// <summary>
    /// Schedules the frontier of every chunk that finished generating, or was restored from a
    /// save, since the last call (<see cref="FluidWakeScanner.WakeLoadedChunks"/>). Main thread,
    /// called once a frame right before <see cref="Update"/> so the cells it schedules ride the
    /// same tick as this frame's block edits.
    /// </summary>
    public void WakeLoadedChunks(ChunkManager chunks) => _wake.WakeLoadedChunks(chunks);

    /// <summary>
    /// Advances the fluid simulation. A new tick starts at most every
    /// <see cref="SimulationConfig.FluidTickSeconds"/> and at most
    /// <see cref="SimulationConfig.MaxFluidTicksPerFrame"/> times per frame, but a tick's
    /// cells are processed in slices of <see cref="SimulationConfig.MaxFluidCellsPerFrame"/>
    /// so a chunk-load wake storm spreads over several frames instead of running in one.
    /// Leftover time past the tick budget is dropped, so a hitch can never leave the simulation
    /// spiraling to catch up.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod = null)
    {
        _hardened.Clear();
        _accumulator += deltaTime;

        _cells.BeginFrame(chunks, lod);

        var ticks = 0;
        var processed = 0;
        while (processed < SimulationConfig.MaxFluidCellsPerFrame)
        {
            // The previous tick is fully processed, so start the next one when it is due
            if (_tickCursor >= _tickCells.Count)
            {
                if (_accumulator < SimulationConfig.FluidTickSeconds
                    || ticks >= SimulationConfig.MaxFluidTicksPerFrame)
                    break;

                _accumulator -= SimulationConfig.FluidTickSeconds;
                ticks++;
                BeginTick();
                if (_tickCells.Count == 0) continue;
            }

            // Spend what is left of the frame's cell budget on the current tick, then yield
            var slice = Math.Min(SimulationConfig.MaxFluidCellsPerFrame - processed,
                _tickCells.Count - _tickCursor);
            var end = _tickCursor + slice;
            for (; _tickCursor < end; _tickCursor++)
                TickCell(_tickCells[_tickCursor], drops);
            processed += slice;

            if (_tickCursor < _tickCells.Count) break;
        }

        if (_accumulator >= SimulationConfig.FluidTickSeconds) _accumulator = 0f;

        _cells.FlushDirty(chunks);
    }

    /// <summary>
    /// Starts a new tick: snapshots the cells scheduled since the last one into
    /// <see cref="_tickCells"/>, which <see cref="Update"/> then walks across the following
    /// frames.
    /// </summary>
    private void BeginTick()
    {
        _tickIndex++;

        _tickCells.Clear();
        _schedule.DrainInto(_tickCells);
        _tickCursor = 0;
    }

    /// <summary>
    /// Writes a cell outside the tick and remeshes it right away: what a bucket does. Also
    /// schedules the neighborhood, so the flow starts on the next tick. Returns false when the
    /// cell's chunk is not loaded or the cell holds an unbreakable block, which a bucket never
    /// scoops or pours over.
    /// </summary>
    public bool SetCellImmediate(Vector3 cell, ushort id, byte state, ChunkManager chunks)
    {
        var x = (int)cell.X;
        var y = (int)cell.Y;
        var z = (int)cell.Z;

        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null) return false;
        int lx = x & Chunk.SizeMask, ly = y & Chunk.SizeMask, lz = z & Chunk.SizeMask;
        if (BlockType.IsUnbreakable(chunk.GetLocalBlock(lx, ly, lz))) return false;

        chunk.SetBlockRaw(lx, ly, lz, id, state);

        var chunkPos = VoxelCoords.ChunkPosOf(x, y, z);
        chunks.ImmediateRemeshWithBorders(chunkPos, cell);

        // Border neighbors were already remeshed synchronously above; they still join the
        // dirty set so their light catches up through the async budget.
        var chunkKey = ChunkKey.PackBlock(x, y, z);
        _borderScratch.Clear();
        ChunkCellCache.AddTouchedChunks(x, y, z, _borderScratch);
        foreach (var key in _borderScratch)
        {
            if (key == chunkKey) continue;
            chunks.MarkDirty(ChunkKey.ToVector3(key));
        }

        ScheduleAround(x, y, z);
        return true;
    }

    /// <summary>Recomputes one scheduled cell's level, then spreads it.</summary>
    private void TickCell(long key, DropManager drops)
    {
        CellKey.Unpack(key, out var x, out var y, out var z);

        // An unloaded cell is dropped silently: nothing has to be remembered for it, so
        // chunk unloading needs no hook here
        if (!_cells.TryGetCell(x, y, z, out var id, out var state)) return;
        if (!BlockType.IsFluid(id)) return;

        // Slow fluids sit this tick out and ride the next one instead of losing their turn
        var divisor = BlockType.FluidTickDivisorById[id];
        if (divisor > 1 && _tickIndex % divisor != 0)
        {
            _schedule.Requeue(key);
            return;
        }

        // Contact hardening runs before the level rules: lava beside water becomes
        // cobblestone instead of flowing or lingering for another tick
        var hardensTo = BlockType.HardensToById[id];
        if (hardensTo != 0 && _neighborhood.TouchesFluid(x, y, z, BlockType.HardensTouchingById[id]))
        {
            _cells.Write(x, y, z, hardensTo, 0);
            _hardened.Add(new Vector3(x, y, z));
            ScheduleAround(x, y, z);
            return;
        }

        // Mud watches settled water, so it runs after the early-outs: skipped slow-fluid
        // ticks and hardening cells never pay the 26-neighborhood scan, and non-water
        // fluids skip it inside (water neighbors convert the dirt on their own tick).
        _mud.TryConvertMudAround(x, y, z, id);

        // Sources are infinite: they never thin out and never dry up
        if (!FluidState.IsSource(state))
        {
            var newState = _neighborhood.ComputeState(x, y, z, id);
            if (newState == FluidNeighborhood.StateNone)
            {
                // Cut off from its supply: the cell drains
                _cells.Write(x, y, z, 0, 0);
                ScheduleAround(x, y, z);
                return;
            }

            if (newState != state)
            {
                state = (byte)newState;
                _cells.Write(x, y, z, id, state);
                ScheduleAround(x, y, z);
            }
        }

        _spread.Spread(x, y, z, id, state, drops);
    }
}
