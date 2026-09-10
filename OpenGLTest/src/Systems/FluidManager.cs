using System;
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
/// edit path in the game therefore has to call <see cref="ScheduleAround(Vector3)"/>, or the
/// water beside the edit will not notice it.
///
/// Worldgen water gets the same treatment from <see cref="WakeLoadedChunks"/>: when a chunk
/// streams in, its whole water/air frontier is scheduled — the interior found by the generation
/// worker (<see cref="Chunk.FluidFrontier"/>), the borders scanned here once both chunks are
/// loaded — so a sea that a cave or an entrance breached pours into it instead of standing
/// there as a wall. Once that has settled it costs nothing again.
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

    // How many ticks have run. Fluids with a tick divisor above 1 (lava: 2) only process
    // when the index divides evenly, so they flow slower while sharing the one schedule.
    private int _tickIndex;

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

    // Dirt-to-mud ids, resolved once content is loaded. Every ticked fluid cell checks
    // its dirt neighbours against the mud condition right away, so settled water still
    // converts dirt the moment an edit schedules it, with no timers to keep.
    private byte _dirtId;
    private byte _mudId;
    private byte _waterId;
    private bool _mudIdsResolved;

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
    /// Schedules the water/air frontier of every chunk that finished generating since the last
    /// call, so a sea breached by a cave or an entrance starts pouring in the moment it loads
    /// instead of standing there as a wall. Interior frontiers were found on the generation
    /// worker (<see cref="Chunk.FluidFrontier"/>); the borders need both chunks loaded, so they
    /// are scanned here. At most <see cref="WorldGenerationConfig.MaxWakeChunksPerFrame"/>
    /// chunks are woken per call; the rest wait in the queue for the next frame.
    ///
    /// Main thread, called once a frame right before <see cref="Update"/> so the cells it
    /// schedules ride the same tick as this frame's block edits.
    /// </summary>
    public void WakeLoadedChunks(ChunkManager chunks)
    {
        var woken = 0;
        while (woken < WorldGenerationConfig.MaxWakeChunksPerFrame && chunks.TryDequeueLoaded(out var chunk))
        {
            woken++;
            // An unload can race the queue: only the chunk still registered at its position
            // owns those cells, and a stale one's blocks are nobody's business
            if (!chunks.Chunks.TryGetValue(chunk.Position, out var live) || live != chunk) continue;

            var frontier = chunk.FluidFrontier;
            if (frontier != null)
            {
                foreach (var index in frontier)
                {
                    // Scheduling the fluid cell alone is enough: its tick runs Spread, and Flow
                    // reschedules the neighbourhood of everything it writes into
                    ScheduleLocal(chunk,
                        index >> (2 * Chunk.SizeShift),
                        (index >> Chunk.SizeShift) & Chunk.SizeMask,
                        index & Chunk.SizeMask);
                }
            }

            // Generated water never sits above sea level, and a chunk that just streamed in
            // holds nothing but generated blocks, so a chunk entirely above it can neither hold
            // water nor border any — the block edit paths cover player-placed water up there.
            // Note this is not the same test as an empty frontier: a chunk can be solid water
            // inside and still face air across a border.
            if (chunk.LowerBoundY > WorldGenerationConfig.SeaLevel) continue;

            WakeBorders(chunk, chunks);
        }
    }

    /// <summary>
    /// Schedules the fluid side of every fluid/flowable pair across the faces this chunk shares
    /// with its loaded neighbours, in whichever chunk the fluid actually lives. Blocks are read
    /// straight off both chunks (they are loaded, so their arrays are there) rather than through
    /// <c>GetBlockGlobal</c>, which would call an unloaded chunk air.
    /// </summary>
    private void WakeBorders(Chunk chunk, ChunkManager chunks)
    {
        var pos = chunk.Position;

        foreach (var (dx, _, dz) in Horizontals)
        {
            if (!TryGetLoadedNeighbour(chunks, pos, dx, 0, dz, out var neighbour)) continue;

            // The two 16x16 planes that touch: this chunk's face cells sit at `near` on the
            // axis the face cuts, the neighbour's directly across at `far`
            var near = dx + dz > 0 ? Chunk.SizeMask : 0;
            var far = Chunk.SizeMask - near;

            for (var ly = 0; ly < Chunk.Size; ly++)
            for (var t = 0; t < Chunk.Size; t++)
            {
                // t sweeps the horizontal axis the face does not cut
                var hx = dx != 0 ? near : t;
                var hz = dx != 0 ? t : near;
                var nx = dx != 0 ? far : t;
                var nz = dx != 0 ? t : far;

                var here = chunk.GetLocalBlock(hx, ly, hz);
                var there = neighbour.GetLocalBlock(nx, ly, nz);

                if (BlockType.IsFluid(here) && IsFlowTarget(there)) ScheduleLocal(chunk, hx, ly, hz);
                else if (BlockType.IsFluid(there) && IsFlowTarget(here)) ScheduleLocal(neighbour, nx, ly, nz);
            }
        }

        // Water only ever falls through a vertical face, so each of these schedules one side:
        // the fluid sitting over the hole, never the air sitting over the fluid.
        if (TryGetLoadedNeighbour(chunks, pos, 0, -1, 0, out var below))
        {
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                if (BlockType.IsFluid(chunk.GetLocalBlock(lx, 0, lz))
                    && IsFlowTarget(below.GetLocalBlock(lx, Chunk.SizeMask, lz)))
                    ScheduleLocal(chunk, lx, 0, lz);
            }
        }

        if (TryGetLoadedNeighbour(chunks, pos, 0, 1, 0, out var above))
        {
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                if (BlockType.IsFluid(above.GetLocalBlock(lx, 0, lz))
                    && IsFlowTarget(chunk.GetLocalBlock(lx, Chunk.SizeMask, lz)))
                    ScheduleLocal(above, lx, 0, lz);
            }
        }
    }

    /// <summary>The face neighbour at the offset, when it exists and its blocks are generated.</summary>
    private static bool TryGetLoadedNeighbour(ChunkManager chunks, Vector3 pos, int dx, int dy, int dz,
        out Chunk neighbour)
    {
        return chunks.Chunks.TryGetValue(pos + new Vector3(dx, dy, dz), out neighbour)
               && neighbour.IsBlockDataReady;
    }

    /// <summary>Schedules a chunk-local cell for the next tick.</summary>
    private void ScheduleLocal(Chunk chunk, int lx, int ly, int lz) =>
        _next.Add(Pack((int)chunk.Position.X * Chunk.Size + lx,
            (int)chunk.Position.Y * Chunk.Size + ly,
            (int)chunk.Position.Z * Chunk.Size + lz));

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
        _tickIndex++;

        foreach (var key in _pending)
        {
            Unpack(key, out var x, out var y, out var z);

            // An unloaded cell is dropped silently: nothing has to be remembered for it, so
            // chunk unloading needs no hook here
            if (!chunks.TryGetCell(x, y, z, out var id, out var state)) continue;
            if (!BlockType.IsFluid(id)) continue;

            TryConvertMudAround(x, y, z, chunks);

            // Slow fluids sit this tick out and ride the next one instead of losing their turn
            var divisor = BlockType.FluidTickDivisorById[id];
            if (divisor > 1 && _tickIndex % divisor != 0)
            {
                _next.Add(key);
                continue;
            }

            // Contact hardening runs before the level rules: lava beside water becomes
            // cobblestone instead of flowing or lingering for another tick
            var hardensTo = BlockType.HardensToById[id];
            if (hardensTo != 0 && TouchesFluid(x, y, z, BlockType.HardensTouchingById[id], chunks))
            {
                Write(x, y, z, hardensTo, 0, chunks);
                ScheduleAround(x, y, z);
                continue;
            }

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

        if (sources >= 2 && BlockType.FluidInfiniteById[fluid])
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

        var range = BlockType.FluidRangeById[fluid];
        var newAmount = maxAmount - 1;
        if (newAmount <= 0 || newAmount < 8 - range) return StateNone;

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

        // A flow that would land touching the fluid that hardens it arrives hardened:
        // lava reaching water comes out as cobblestone, not as a one-tick lava cell
        byte writeId = fluid;
        byte writeState = (byte)state;
        var hardensTo = BlockType.HardensToById[fluid];
        if (hardensTo != 0 && TouchesFluid(x, y, z, BlockType.HardensTouchingById[fluid], chunks))
        {
            writeId = hardensTo;
            writeState = 0;
        }
        else
        {
            // ...and so does a flow settling against a fluid that hardens on its touch:
            // water reaching lava crusts to cobblestone too, whichever side ticks first,
            // so the outcome never depends on the schedule's iteration order
            var crust = HardenedByNeighbour(x, y, z, fluid, chunks);
            if (crust != 0)
            {
                writeId = crust;
                writeState = 0;
            }
        }

        if (!chunks.TryGetCell(x, y, z, out var previous, out var previousState)) return;

        if (previous != 0)
        {
            drops.SpawnLoot(previous, new Vector3(x, y, z),
                DropManager.PileCount(previous, x, z, previousState));
            chunks.UnmarkPlayerPlaced(x, y, z);
        }

        Write(x, y, z, writeId, writeState, chunks);
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
        return chunks.TryGetCell(x, y, z, out var id, out _) && IsFlowTarget(id);
    }

    /// <summary>
    /// Whether a block a fluid already knows is loaded is somewhere it may flow: not fluid
    /// itself, and either air or a fragile block the flow washes away. Shared with
    /// <see cref="Chunk.FluidFrontier"/>'s worker-thread scan so both halves of the wake-up
    /// agree with <see cref="CanFlowInto"/> on what counts as somewhere to go.
    /// </summary>
    internal static bool IsFlowTarget(byte id) => !BlockType.IsFluid(id) && BlockType.IsReplaceable(id);

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

    /// <summary>Whether any of the six face neighbours of a cell holds the given fluid.
    /// Unloaded neighbours read as absent, never as contact.</summary>
    private static bool TouchesFluid(int x, int y, int z, byte fluidId, ChunkManager chunks)    {
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (chunks.TryGetCell(x + dx, y + dy, z + dz, out var id, out _) && id == fluidId)
                return true;
        }

        return (chunks.TryGetCell(x, y + 1, z, out var above, out _) && above == fluidId)
               || (chunks.TryGetCell(x, y - 1, z, out var below, out _) && below == fluidId);
    }

    /// <summary>
    /// Block id a flow of <paramref name="fluid"/> settling at a cell hardens into when a
    /// neighbouring fluid hardens on its touch (water reaching lava crusts to the lava's
    /// cobblestone), or 0 when no neighbour does. Unloaded neighbours harden nothing.
    /// </summary>
    private static byte HardenedByNeighbour(int x, int y, int z, byte fluid, ChunkManager chunks)
    {
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (chunks.TryGetCell(x + dx, y + dy, z + dz, out var id, out _)
                && BlockType.IsFluid(id)
                && BlockType.HardensTouchingById[id] == fluid
                && BlockType.HardensToById[id] != 0)
                return BlockType.HardensToById[id];
        }

        if (chunks.TryGetCell(x, y + 1, z, out var above, out _)
            && BlockType.IsFluid(above)
            && BlockType.HardensTouchingById[above] == fluid
            && BlockType.HardensToById[above] != 0)
            return BlockType.HardensToById[above];

        if (chunks.TryGetCell(x, y - 1, z, out var below, out _)
            && BlockType.IsFluid(below)
            && BlockType.HardensTouchingById[below] == fluid
            && BlockType.HardensToById[below] != 0)
            return BlockType.HardensToById[below];

        return 0;
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

    /// <summary>
    /// Turns the dirt around a ticked fluid cell to mud wherever it meets the mud condition
    /// (water above, enough water across the 26-neighbourhood). Instant: no wet-time is kept,
    /// so drying out needs no handling — mud simply stays mud. Conversions write through
    /// <see cref="Write"/> like any fluid edit, so their remeshes join the tick's batch.
    /// </summary>
    private void TryConvertMudAround(int x, int y, int z, ChunkManager chunks)
    {
        if (!MudIds(out var dirt, out var mud, out var water)) return;

        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!chunks.TryGetCell(nx, ny, nz, out var id, out _) || id != dirt) continue;
            if (!IsMudWet(nx, ny, nz, water, chunks)) continue;

            Write(nx, ny, nz, mud, 0, chunks);
            ScheduleAround(nx, ny, nz);
        }
    }

    /// <summary>
    /// Whether a dirt cell counts as submerged: a water cell directly above it, and enough
    /// water across the whole 26-neighbourhood. Unloaded neighbours read as absent.
    /// </summary>
    private static bool IsMudWet(int x, int y, int z, byte water, ChunkManager chunks)
    {
        if (!chunks.TryGetCell(x, y + 1, z, out var above, out _) || above != water) return false;

        var count = 0;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            if (chunks.TryGetCell(x + dx, y + dy, z + dz, out var id, out _) && id == water
                && ++count >= WorldGenerationConfig.MudWaterNeighbours)
                return true;
        }

        return false;
    }

    /// <summary>Dirt, mud and water ids, resolved once content is loaded.</summary>
    private bool MudIds(out byte dirt, out byte mud, out byte water)
    {
        if (!_mudIdsResolved)
        {
            try
            {
                _dirtId = ContentRegistry.RequireBlock("dirt").Id;
                _mudId = ContentRegistry.RequireBlock("mud").Id;
                _waterId = ContentRegistry.RequireBlock("water").Id;
            }
            catch (InvalidOperationException)
            {
                dirt = mud = water = 0;
                return false;
            }

            _mudIdsResolved = true;
        }

        dirt = _dirtId;
        mud = _mudId;
        water = _waterId;
        return true;
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
