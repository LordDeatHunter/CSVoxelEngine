using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The fluid simulation: a fixed tick (<see cref="WorldGenerationConfig.FluidTickSeconds"/>)
/// over a set of scheduled cells, mirroring Minecraft's water rules — infinite sources, a
/// flowing skirt that thins by one level per block, columns that fall, and a slope search that
/// makes water prefer the nearest hole.
///
/// Only scheduled cells do any work, and a cell reschedules its neighbourhood exactly when it
/// changes or writes into a neighbour, so a settled body of water costs nothing. Every block
/// edit path in the game therefore has to call <see cref="ScheduleAround(Vector3)"/> — water
/// next to untouched worldgen air stays static until something adjacent is disturbed, the same
/// way Minecraft's does.
///
/// Cells are read through <see cref="ChunkManager.TryGetCell"/> and never through
/// <c>GetBlockGlobal</c>: the latter reports unloaded chunks as air, which would let water
/// pour into space that has not been generated yet.
///
/// Main thread only: it writes block data and schedules remeshes.
/// </summary>
public class FluidManager
{
    // Cell keys pack a world position into 21 bits per axis (±1,048,576 blocks), which is far
    // beyond anything the chunk streamer will ever load.
    private const int PackBits = 21;
    private const long PackMask = (1L << PackBits) - 1;

    // Sentinel slope distance meaning "no hole found down this branch" (Minecraft's 1000).
    // Leaving it above every real distance makes the "keep the minimum" filter degrade into
    // "keep every passable direction" on flat ground for free.
    private const int NoSlope = 1000;

    // ComputeState's "this cell holds no fluid any more" answer
    private const int StateNone = -1;

    private static readonly (int X, int Y, int Z)[] Horizontals =
        [(1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1)];

    private float _accumulator;

    // Cells ticking now and cells scheduled for the next tick; swapped every tick so a cell
    // scheduled while ticking is handled on the following one (no unbounded cascade per tick).
    private HashSet<long> _pending = [];
    private HashSet<long> _next = [];

    // Chunks touched by this tick's writes, remeshed once at the end of it
    private readonly HashSet<Vector3> _dirtyChunks = [];

    // Scratch for SetCellImmediate's border neighbours (kept off the tick's set)
    private readonly HashSet<Vector3> _borderScratch = [];

    // Reused by the slope search: nothing it calls chooses directions again, so one buffer
    // keeps a draining pond from allocating per cell per tick
    private readonly List<(int X, int Y, int Z)> _spreadDirections = new(4);

    /// <summary>Schedules a cell and its six face neighbours for the next tick. Every path that
    /// changes a block must call this, or water beside the edit will not notice it.</summary>
    public void ScheduleAround(Vector3 cell) => ScheduleAround((int)cell.X, (int)cell.Y, (int)cell.Z);

    /// <summary>Schedules a cell and its six face neighbours for the next tick.</summary>
    public void ScheduleAround(int x, int y, int z)
    {
        _next.Add(Pack(x, y, z));
        _next.Add(Pack(x + 1, y, z));
        _next.Add(Pack(x - 1, y, z));
        _next.Add(Pack(x, y + 1, z));
        _next.Add(Pack(x, y - 1, z));
        _next.Add(Pack(x, y, z + 1));
        _next.Add(Pack(x, y, z - 1));
    }

    /// <summary>
    /// Schedules every cell of the inclusive integer box for the next tick. Used by a TNT blast,
    /// which clears a whole sphere at once and needs the water around all of it to pour in.
    /// </summary>
    public void ScheduleBox(Vector3 min, Vector3 max)
    {
        for (var x = (int)min.X; x <= (int)max.X; x++)
        for (var y = (int)min.Y; y <= (int)max.Y; y++)
        for (var z = (int)min.Z; z <= (int)max.Z; z++)
            _next.Add(Pack(x, y, z));
    }

    /// <summary>
    /// Runs whole fluid ticks at a fixed rate, at most
    /// <see cref="WorldGenerationConfig.MaxFluidTicksPerFrame"/> of them per frame. Leftover
    /// time past that budget is dropped rather than carried, so a hitch (or a chunk-loading
    /// stall) can never leave the simulation spiralling to catch up.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        _accumulator += deltaTime;
        if (_accumulator < WorldGenerationConfig.FluidTickSeconds) return;

        var ticks = 0;
        while (_accumulator >= WorldGenerationConfig.FluidTickSeconds
               && ticks < WorldGenerationConfig.MaxFluidTicksPerFrame)
        {
            _accumulator -= WorldGenerationConfig.FluidTickSeconds;
            ticks++;
            Tick(chunks, drops);
        }

        if (_accumulator >= WorldGenerationConfig.FluidTickSeconds) _accumulator = 0f;
    }

    /// <summary>
    /// Writes a cell outside the tick and remeshes it right away: what a bucket does. Also
    /// schedules the neighbourhood, so the flow starts on the next tick. Returns false when the
    /// cell's chunk is not loaded.
    /// </summary>
    public bool SetCellImmediate(Vector3 cell, byte id, byte state, ChunkManager chunks)
    {
        var x = (int)cell.X;
        var y = (int)cell.Y;
        var z = (int)cell.Z;

        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null) return false;

        chunk.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);

        var chunkPos = ChunkPosOf(x, y, z);
        chunks.ImmediateRemesh(chunkPos);

        // A cell on a chunk border shows up in the neighbour's mesh too; those can wait for
        // the async budget
        _borderScratch.Clear();
        AddTouchedChunks(x, y, z, _borderScratch);
        foreach (var pos in _borderScratch)
        {
            if (pos != chunkPos) chunks.MarkDirty(pos);
        }

        ScheduleAround(x, y, z);
        return true;
    }

    /// <summary>One fluid tick: every scheduled cell recomputes its own level, then spreads.</summary>
    private void Tick(ChunkManager chunks, DropManager drops)
    {
        (_pending, _next) = (_next, _pending);
        _next.Clear();
        _dirtyChunks.Clear();

        foreach (var key in _pending)
        {
            Unpack(key, out var x, out var y, out var z);

            // An unloaded cell is dropped silently: nothing has to be remembered for it, so
            // chunk unloading needs no hook here
            if (!chunks.TryGetCell(x, y, z, out var id, out var state)) continue;
            if (!BlockType.IsFluid(id)) continue;

            // Sources are infinite: they never thin out and never dry up
            if (!FluidState.IsSource(state))
            {
                var newState = ComputeState(x, y, z, id, chunks);
                if (newState == StateNone)
                {
                    // Cut off from its supply: the cell drains
                    Write(x, y, z, 0, 0, chunks);
                    ScheduleAround(x, y, z);
                    continue;
                }

                if (newState != state)
                {
                    state = (byte)newState;
                    Write(x, y, z, id, state, chunks);
                    ScheduleAround(x, y, z);
                }
            }

            Spread(x, y, z, id, state, chunks, drops);
        }

        foreach (var chunkPos in _dirtyChunks)
            chunks.MarkDirty(chunkPos);

        _pending.Clear();
    }

    /// <summary>
    /// The state a non-source fluid cell should hold, given its surroundings (Minecraft's
    /// <c>getNewLiquid</c>): <see cref="StateNone"/> when nothing feeds it any more,
    /// <see cref="FluidState.Source"/> when two sources meet over solid ground (the infinite
    /// 2x2 pool), <see cref="FluidState.Falling"/> when fed from above, else the thinnest
    /// flowing level its neighbours can support.
    /// </summary>
    private static int ComputeState(int x, int y, int z, byte fluid, ChunkManager chunks)
    {
        var maxAmount = 0;
        var sources = 0;

        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (!chunks.TryGetCell(x + dx, y + dy, z + dz, out var id, out var state)) continue;
            if (id != fluid) continue;

            if (FluidState.IsSource(state)) sources++;
            var amount = FluidState.Amount(state);
            if (amount > maxAmount) maxAmount = amount;
        }

        if (sources >= 2)
        {
            // Two sources side by side become a source themselves, but only where the water
            // cannot simply drain away underneath
            if (chunks.TryGetCell(x, y - 1, z, out var below, out var belowState)
                && ((below != 0 && !BlockType.IsReplaceable(below) && !BlockType.IsFluid(below))
                    || (below == fluid && FluidState.IsSource(belowState))))
                return FluidState.Source;
        }

        // Water pouring in from above always fills the whole cell
        if (chunks.TryGetCell(x, y + 1, z, out var above, out _) && above == fluid)
            return FluidState.Falling;

        var newAmount = maxAmount - 1;
        if (newAmount <= 0) return StateNone;

        return FluidState.Flowing(8 - newAmount);
    }

    /// <summary>
    /// Pushes water out of a cell. Spreading only ever writes into non-fluid replaceable cells;
    /// a cell that already holds fluid recomputes itself on its own tick, which is how the cell
    /// under fresh water turns into a falling column.
    /// </summary>
    private void Spread(int x, int y, int z, byte fluid, byte state, ChunkManager chunks, DropManager drops)
    {
        if (CanFlowInto(x, y - 1, z, chunks))
        {
            Flow(x, y - 1, z, fluid, chunks, drops);

            // A cell ringed by sources keeps its skirt even while draining downwards
            if (SourceNeighbourCount(x, y, z, fluid, chunks) >= 3)
                SpreadSides(x, y, z, fluid, state, chunks, drops);
        }
        else if (FluidState.IsSource(state) || !IsWaterHole(x, y - 1, z, fluid, chunks))
        {
            // Standing on something it cannot fall through, so it goes sideways
            SpreadSides(x, y, z, fluid, state, chunks, drops);
        }
    }

    /// <summary>Flows into the neighbouring cells the slope search picked out.</summary>
    private void SpreadSides(int x, int y, int z, byte fluid, byte state, ChunkManager chunks, DropManager drops)
    {
        // Level 7 has nothing left to give
        if (FluidState.Amount(state) - 1 <= 0) return;

        foreach (var (dx, dy, dz) in ChooseSpreadDirections(x, y, z, fluid, chunks))
        {
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (CanFlowInto(nx, ny, nz, chunks))
                Flow(nx, ny, nz, fluid, chunks, drops);
        }
    }

    /// <summary>
    /// The horizontal directions water should take out of a cell (Minecraft's <c>getSpread</c>):
    /// the ones whose nearest hole is closest, within
    /// <see cref="WorldGenerationConfig.FluidSlopeSearchDistance"/> blocks. On flat ground no
    /// direction finds a hole, they all tie, and the water spreads evenly.
    /// </summary>
    private List<(int X, int Y, int Z)> ChooseSpreadDirections(int x, int y, int z, byte fluid, ChunkManager chunks)
    {
        var best = NoSlope;
        var chosen = _spreadDirections;
        chosen.Clear();

        foreach (var (dx, dy, dz) in Horizontals)
        {
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!CanPassThrough(nx, ny, nz, fluid, chunks)) continue;

            var distance = IsWaterHole(nx, ny - 1, nz, fluid, chunks)
                ? 0
                : SlopeDistance(nx, ny, nz, 1, (-dx, -dy, -dz), fluid, chunks);

            if (distance < best)
            {
                best = distance;
                chosen.Clear();
            }

            if (distance == best) chosen.Add((dx, dy, dz));
        }

        return chosen;
    }

    /// <summary>
    /// Blocks from <paramref name="depth"/> to the nearest hole reachable through passable
    /// cells, or <see cref="NoSlope"/> when none is within
    /// <see cref="WorldGenerationConfig.FluidSlopeSearchDistance"/>. Never turns back on
    /// itself (<paramref name="exclude"/>), so the search fans out.
    /// </summary>
    private static int SlopeDistance(int x, int y, int z, int depth, (int X, int Y, int Z) exclude,
        byte fluid, ChunkManager chunks)
    {
        var best = NoSlope;

        foreach (var (dx, dy, dz) in Horizontals)
        {
            if ((dx, dy, dz) == exclude) continue;

            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!CanPassThrough(nx, ny, nz, fluid, chunks)) continue;

            if (IsWaterHole(nx, ny - 1, nz, fluid, chunks)) return depth;

            if (depth >= WorldGenerationConfig.FluidSlopeSearchDistance) continue;

            var distance = SlopeDistance(nx, ny, nz, depth + 1, (-dx, -dy, -dz), fluid, chunks);
            if (distance < best) best = distance;
        }

        return best;
    }

    /// <summary>
    /// Fills a cell with fluid at the level its surroundings support, popping whatever fragile
    /// block the flow washed away (a pebble drops its flint) and rescheduling the neighbourhood.
    /// </summary>
    private void Flow(int x, int y, int z, byte fluid, ChunkManager chunks, DropManager drops)
    {
        var state = ComputeState(x, y, z, fluid, chunks);

        // Nothing actually reaches this cell (it would drain again next tick): leave it alone
        // rather than washing a block away for a drop of water that never arrives
        if (state == StateNone) return;

        if (!chunks.TryGetCell(x, y, z, out var previous, out _)) return;

        if (previous != 0)
        {
            drops.SpawnLoot(previous, new Vector3(x, y, z));
            chunks.UnmarkPlayerPlaced(x, y, z);
        }

        Write(x, y, z, fluid, (byte)state, chunks);
        ScheduleAround(x, y, z);
    }

    /// <summary>
    /// Writes a cell's id and state with no remesh of its own, queueing the chunks whose mesh
    /// the change shows up in for the batch at the end of the tick. Fluids pass light and cast
    /// no ambient occlusion, so only the cell's own chunk and the chunks across a border it
    /// touches are affected — never the whole light radius.
    /// </summary>
    private void Write(int x, int y, int z, byte id, byte state, ChunkManager chunks)
    {
        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null) return;

        chunk.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        AddTouchedChunks(x, y, z, _dirtyChunks);
    }

    /// <summary>Whether fluid may replace what is in a cell: loaded, not already fluid, and
    /// either air or a fragile block the flow washes away.</summary>
    private static bool CanFlowInto(int x, int y, int z, ChunkManager chunks)
    {
        return chunks.TryGetCell(x, y, z, out var id, out _)
               && !BlockType.IsFluid(id)
               && BlockType.IsReplaceable(id);
    }

    /// <summary>Whether a cell would let fluid drain into it: replaceable, or already the same
    /// fluid. What the slope search calls a "hole".</summary>
    private static bool IsWaterHole(int x, int y, int z, byte fluid, ChunkManager chunks)
    {
        return chunks.TryGetCell(x, y, z, out var id, out _)
               && (BlockType.IsReplaceable(id) || id == fluid);
    }

    /// <summary>Whether the slope search may travel through a cell: replaceable, or the same
    /// fluid but not a source (a source is a destination, not a corridor).</summary>
    private static bool CanPassThrough(int x, int y, int z, byte fluid, ChunkManager chunks)
    {
        if (!chunks.TryGetCell(x, y, z, out var id, out var state)) return false;
        return BlockType.IsReplaceable(id) || (id == fluid && !FluidState.IsSource(state));
    }

    /// <summary>Horizontally adjacent source cells of the same fluid.</summary>
    private static int SourceNeighbourCount(int x, int y, int z, byte fluid, ChunkManager chunks)
    {
        var count = 0;
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (chunks.TryGetCell(x + dx, y + dy, z + dz, out var id, out var state)
                && id == fluid && FluidState.IsSource(state))
                count++;
        }

        return count;
    }

    /// <summary>Adds the chunk owning a cell, plus the face-adjacent chunk on every axis where
    /// the cell sits on a chunk border, to <paramref name="into"/>.</summary>
    private static void AddTouchedChunks(int x, int y, int z, HashSet<Vector3> into)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        into.Add(new Vector3(cx, cy, cz));

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;

        if (lx == 0) into.Add(new Vector3(cx - 1, cy, cz));
        else if (lx == Chunk.SizeMask) into.Add(new Vector3(cx + 1, cy, cz));

        if (ly == 0) into.Add(new Vector3(cx, cy - 1, cz));
        else if (ly == Chunk.SizeMask) into.Add(new Vector3(cx, cy + 1, cz));

        if (lz == 0) into.Add(new Vector3(cx, cy, cz - 1));
        else if (lz == Chunk.SizeMask) into.Add(new Vector3(cx, cy, cz + 1));
    }

    private static Vector3 ChunkPosOf(int x, int y, int z) =>
        new(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    private static long Pack(int x, int y, int z) =>
        ((x & PackMask) << (2 * PackBits)) | ((y & PackMask) << PackBits) | (z & PackMask);

    private static void Unpack(long key, out int x, out int y, out int z)
    {
        x = SignExtend((int)((key >> (2 * PackBits)) & PackMask));
        y = SignExtend((int)((key >> PackBits) & PackMask));
        z = SignExtend((int)(key & PackMask));
    }

    // A packed axis keeps only the low PackBits, so its sign bit has to be pushed back up
    private static int SignExtend(int value) => (value << (32 - PackBits)) >> (32 - PackBits);
}
