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
/// Cells are read through <see cref="TryGetCell"/> and never through
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

    // Cells scheduled since the last tick started. Every write, edit and chunk wake adds here;
    // a tick snapshots it into _tickCells below, so a cell scheduled while ticking is handled on
    // the following tick (no unbounded cascade within one).
    private readonly HashSet<long> _next = [];

    // The current tick's cells. A tick may span several frames: _tickCursor walks this list one
    // per-frame slice at a time, so a chunk-load wake storm spreads across frames instead of
    // running in one, while the simulation still keeps up with the wake.
    private readonly List<long> _tickCells = [];
    private int _tickCursor;

    // Chunks touched by this frame's tick slice, remeshed once the slice is done. Packed chunk
    // keys rather than Vector3 so the set never pays the float-vector hashing tax.
    private readonly HashSet<long> _dirtyChunks = [];

    // Scratch for SetCellImmediate's border neighbours (kept off the tick's set)
    private readonly HashSet<long> _borderScratch = [];

    // Reused by the slope search: nothing it calls chooses directions again, so one buffer
    // keeps a draining pond from allocating per cell per tick
    private readonly List<(int X, int Y, int Z)> _spreadDirections = new(4);

    // The manager the current tick reads and writes through; set by Update before the ticks run
    private ChunkManager _chunks = null!;

    // Direct-mapped chunk-lookup cache for one tick. Every simulation cell read goes through
    // TryGetCell; without this each read is a Vector3 hash lookup in the chunk dictionary. A
    // slope search spans at most a handful of chunks, so 16 slots hold every key it can reach,
    // validated by the packed chunk key (always non-negative, so -1 is a safe empty sentinel).
    // The keys are cleared at the top of every Tick: an unload can replace a chunk between
    // frames, and a cached reference must never outlive that. Only the Chunk is cached, never
    // its block/state arrays — SetBlockRaw swaps in a fresh state array on the first non-zero
    // write, so a cached array pointer would miss later fluid state.
    private const int ChunkCacheSize = 16;
    private const int ChunkCacheMask = ChunkCacheSize - 1;
    private readonly Chunk?[] _chunkCache = new Chunk?[ChunkCacheSize];
    private readonly long[] _chunkCacheKeys = new long[ChunkCacheSize];

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
        var backlog = WorldGenerationConfig.MaxFluidBacklogCells;
        while (woken < WorldGenerationConfig.MaxWakeChunksPerFrame
               && _next.Count < backlog
               && chunks.TryDequeueLoaded(out var chunk))
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
    /// Advances the fluid simulation. A new tick starts at most every
    /// <see cref="WorldGenerationConfig.FluidTickSeconds"/> and at most
    /// <see cref="WorldGenerationConfig.MaxFluidTicksPerFrame"/> times per frame, but a tick's
    /// cells are processed in slices of <see cref="WorldGenerationConfig.MaxFluidCellsPerFrame"/>
    /// so a chunk-load wake storm spreads over several frames instead of running in one.
    /// Leftover time past the tick budget is dropped, so a hitch can never leave the simulation
    /// spiralling to catch up.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        _accumulator += deltaTime;

        // The tick's helpers read through this and its chunk cache, so they need no manager
        // argument of their own
        _chunks = chunks;

        // The chunk cache is valid for one frame only: an unload can replace a chunk between
        // frames, and a reference cached before it must never be handed back
        _chunkCacheKeys.AsSpan().Fill(-1L);

        var ticks = 0;
        var processed = 0;
        while (processed < WorldGenerationConfig.MaxFluidCellsPerFrame)
        {
            // The previous tick is fully processed, so start the next one when it is due
            if (_tickCursor >= _tickCells.Count)
            {
                if (_accumulator < WorldGenerationConfig.FluidTickSeconds
                    || ticks >= WorldGenerationConfig.MaxFluidTicksPerFrame)
                    break;

                _accumulator -= WorldGenerationConfig.FluidTickSeconds;
                ticks++;
                BeginTick();
                if (_tickCells.Count == 0) continue;
            }

            // Spend what is left of the frame's cell budget on the current tick, then yield
            var slice = Math.Min(WorldGenerationConfig.MaxFluidCellsPerFrame - processed,
                _tickCells.Count - _tickCursor);
            var end = _tickCursor + slice;
            for (; _tickCursor < end; _tickCursor++)
                TickCell(_tickCells[_tickCursor], drops);
            processed += slice;

            if (_tickCursor < _tickCells.Count) break;
        }

        if (_accumulator >= WorldGenerationConfig.FluidTickSeconds) _accumulator = 0f;

        if (_dirtyChunks.Count > 0)
        {
            foreach (var key in _dirtyChunks)
            {
                Unpack(key, out var cx, out var cy, out var cz);
                chunks.MarkDirty(new Vector3(cx, cy, cz));
            }
            _dirtyChunks.Clear();
        }
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
        _tickCells.AddRange(_next);
        _next.Clear();
        _tickCursor = 0;
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
        var chunkKey = Pack(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        _borderScratch.Clear();
        AddTouchedChunks(x, y, z, _borderScratch);
        foreach (var key in _borderScratch)
        {
            if (key == chunkKey) continue;
            Unpack(key, out var cx, out var cy, out var cz);
            chunks.MarkDirty(new Vector3(cx, cy, cz));
        }

        ScheduleAround(x, y, z);
        return true;
    }

    /// <summary>Recomputes one scheduled cell's level, then spreads it.</summary>
    private void TickCell(long key, DropManager drops)
    {
        Unpack(key, out var x, out var y, out var z);

        // An unloaded cell is dropped silently: nothing has to be remembered for it, so
        // chunk unloading needs no hook here
        if (!TryGetCell(x, y, z, out var id, out var state)) return;
        if (!BlockType.IsFluid(id)) return;

        // Slow fluids sit this tick out and ride the next one instead of losing their turn
        var divisor = BlockType.FluidTickDivisorById[id];
        if (divisor > 1 && _tickIndex % divisor != 0)
        {
            _next.Add(key);
            return;
        }

        // Contact hardening runs before the level rules: lava beside water becomes
        // cobblestone instead of flowing or lingering for another tick
        var hardensTo = BlockType.HardensToById[id];
        if (hardensTo != 0 && TouchesFluid(x, y, z, BlockType.HardensTouchingById[id]))
        {
            Write(x, y, z, hardensTo, 0);
            ScheduleAround(x, y, z);
            return;
        }

        // Mud watches settled water, so it runs after the early-outs: skipped slow-fluid
        // ticks and hardening cells never pay the 26-neighbourhood scan, and non-water
        // fluids skip it inside (water neighbours convert the dirt on their own tick).
        TryConvertMudAround(x, y, z, id);

        // Sources are infinite: they never thin out and never dry up
        if (!FluidState.IsSource(state))
        {
            var newState = ComputeState(x, y, z, id);
            if (newState == StateNone)
            {
                // Cut off from its supply: the cell drains
                Write(x, y, z, 0, 0);
                ScheduleAround(x, y, z);
                return;
            }

            if (newState != state)
            {
                state = (byte)newState;
                Write(x, y, z, id, state);
                ScheduleAround(x, y, z);
            }
        }

        Spread(x, y, z, id, state, drops);
    }

    /// <summary>
    /// The state a non-source fluid cell should hold, given its surroundings (Minecraft's
    /// <c>getNewLiquid</c>): <see cref="StateNone"/> when nothing feeds it any more,
    /// <see cref="FluidState.Source"/> when two sources meet over solid ground (the infinite
    /// 2x2 pool), <see cref="FluidState.Falling"/> when fed from above, else the thinnest
    /// flowing level its neighbours can support.
    /// </summary>
    private int ComputeState(int x, int y, int z, byte fluid)
    {
        var maxAmount = 0;
        var sources = 0;

        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (!TryGetCell(x + dx, y + dy, z + dz, out var id, out var state)) continue;
            if (id != fluid) continue;

            if (FluidState.IsSource(state)) sources++;
            var amount = FluidState.Amount(state);
            if (amount > maxAmount) maxAmount = amount;
        }

        if (sources >= 2 && BlockType.FluidInfiniteById[fluid])
        {
            // Two sources side by side become a source themselves, but only where the water
            // cannot simply drain away underneath
            if (TryGetCell(x, y - 1, z, out var below, out var belowState)
                && ((below != 0 && !BlockType.IsReplaceable(below) && !BlockType.IsFluid(below))
                    || (below == fluid && FluidState.IsSource(belowState))))
                return FluidState.Source;
        }

        // Water pouring in from above always fills the whole cell
        if (TryGetCell(x, y + 1, z, out var above, out _) && above == fluid)
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
    private void Spread(int x, int y, int z, byte fluid, byte state, DropManager drops)
    {
        if (CanFlowInto(x, y - 1, z))
        {
            Flow(x, y - 1, z, fluid, drops);

            // A cell ringed by sources keeps its skirt even while draining downwards
            if (SourceNeighbourCount(x, y, z, fluid) >= 3)
                SpreadSides(x, y, z, fluid, state, drops);
        }
        else if (FluidState.IsSource(state) || !IsWaterHole(x, y - 1, z, fluid))
        {
            // Standing on something it cannot fall through, so it goes sideways
            SpreadSides(x, y, z, fluid, state, drops);
        }
    }

    /// <summary>Flows into the neighbouring cells the slope search picked out.</summary>
    private void SpreadSides(int x, int y, int z, byte fluid, byte state, DropManager drops)
    {
        // Level 7 has nothing left to give
        if (FluidState.Amount(state) - 1 <= 0) return;

        // Fast path: spreading only ever writes into an immediate horizontal neighbour
        // (see the CanFlowInto gate on each Flow below), so when none of the four is
        // flowable the slope search can pick nothing writable — skip it entirely.
        // This is exact, not a heuristic: the loop below cannot write otherwise.
        if (!CanFlowInto(x + 1, y, z)
            && !CanFlowInto(x - 1, y, z)
            && !CanFlowInto(x, y, z + 1)
            && !CanFlowInto(x, y, z - 1))
            return;

        foreach (var (dx, dy, dz) in ChooseSpreadDirections(x, y, z, fluid))
        {
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (CanFlowInto(nx, ny, nz))
                Flow(nx, ny, nz, fluid, drops);
        }
    }

    /// <summary>
    /// The horizontal directions water should take out of a cell (Minecraft's <c>getSpread</c>):
    /// the ones whose nearest hole is closest, within
    /// <see cref="WorldGenerationConfig.FluidSlopeSearchDistance"/> blocks. On flat ground no
    /// direction finds a hole, they all tie, and the water spreads evenly.
    /// </summary>
    private List<(int X, int Y, int Z)> ChooseSpreadDirections(int x, int y, int z, byte fluid)
    {
        var best = NoSlope;
        var chosen = _spreadDirections;
        chosen.Clear();

        foreach (var (dx, dy, dz) in Horizontals)
        {
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!CanPassThrough(nx, ny, nz, fluid)) continue;

            int distance;
            if (IsWaterHole(nx, ny - 1, nz, fluid))
            {
                // A hole directly under this neighbour is distance 0, the global minimum
                distance = 0;
            }
            else if (best == 0)
            {
                // No direction can beat a direct hole, and this branch cannot tie at 0
                // either (it has no direct hole), so the deep search is skipped whole
                continue;
            }
            else
            {
                distance = SlopeDistance(nx, ny, nz, 1, (-dx, -dy, -dz), fluid);
            }

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
    private int SlopeDistance(int x, int y, int z, int depth, (int X, int Y, int Z) exclude, byte fluid)
    {
        var best = NoSlope;

        foreach (var (dx, dy, dz) in Horizontals)
        {
            if ((dx, dy, dz) == exclude) continue;

            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!CanPassThrough(nx, ny, nz, fluid)) continue;

            // A hole one step on is the shallowest this call can find, so it wins at once
            if (IsWaterHole(nx, ny - 1, nz, fluid)) return depth;

            // A hole found at depth + 1 is the shallowest any recursive branch can find.
            // Once best is that shallow, a later direction is still checked for a direct
            // hole above, but its recursion can only tie, never beat, so it is skipped.
            if (best == depth + 1) continue;

            if (depth >= WorldGenerationConfig.FluidSlopeSearchDistance) continue;

            var distance = SlopeDistance(nx, ny, nz, depth + 1, (-dx, -dy, -dz), fluid);
            if (distance < best) best = distance;
        }

        return best;
    }

    /// <summary>
    /// Fills a cell with fluid at the level its surroundings support, popping whatever fragile
    /// block the flow washed away (a pebble drops its flint) and rescheduling the neighbourhood.
    /// </summary>
    private void Flow(int x, int y, int z, byte fluid, DropManager drops)
    {
        var state = ComputeState(x, y, z, fluid);

        // Nothing actually reaches this cell (it would drain again next tick): leave it alone
        // rather than washing a block away for a drop of water that never arrives
        if (state == StateNone) return;

        // A flow that would land touching the fluid that hardens it arrives hardened:
        // lava reaching water comes out as cobblestone, not as a one-tick lava cell
        byte writeId = fluid;
        byte writeState = (byte)state;
        var hardensTo = BlockType.HardensToById[fluid];
        if (hardensTo != 0 && TouchesFluid(x, y, z, BlockType.HardensTouchingById[fluid]))
        {
            writeId = hardensTo;
            writeState = 0;
        }
        else
        {
            // ...and so does a flow settling against a fluid that hardens on its touch:
            // water reaching lava crusts to cobblestone too, whichever side ticks first,
            // so the outcome never depends on the schedule's iteration order
            var crust = HardenedByNeighbour(x, y, z, fluid);
            if (crust != 0)
            {
                writeId = crust;
                writeState = 0;
            }
        }

        if (!TryGetCell(x, y, z, out var previous, out var previousState)) return;

        if (previous != 0)
        {
            drops.SpawnLoot(previous, new Vector3(x, y, z),
                DropManager.PileCount(previous, x, z, previousState));
            _chunks.UnmarkPlayerPlaced(x, y, z);
        }

        Write(x, y, z, writeId, writeState);
        ScheduleAround(x, y, z);
    }

    /// <summary>
    /// The loaded chunk owning a cell, through the tick's direct-mapped cache. A miss probes
    /// the chunk dictionary once and stores the reference; because the keys are cleared every
    /// tick, a hit can never hand back a chunk an unload has since replaced. Only the
    /// <see cref="Chunk"/> is cached, never its block or state arrays: <see cref="Chunk.SetBlockRaw"/>
    /// swaps in a fresh state array on the first non-zero write, so a cached array would miss
    /// later state. The miss path goes through <see cref="ChunkManager.ChunkAt"/>, which keeps
    /// the tests' contract that a chunk without ready block data reads as unloaded.
    /// </summary>
    private bool TryGetChunk(int x, int y, int z, out Chunk? chunk)
    {
        var key = Pack(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var slot = (int)((key ^ (key >> 32)) & ChunkCacheMask);

        if (_chunkCacheKeys[slot] != key)
        {
            _chunkCacheKeys[slot] = key;
            chunk = _chunks.ChunkAt(x, y, z);
            _chunkCache[slot] = chunk;
        }
        else
        {
            chunk = _chunkCache[slot];
        }

        return chunk != null;
    }

    /// <summary>
    /// Reads a world cell's block id and state through the tick's chunk cache. The contract
    /// matches <see cref="ChunkManager.TryGetCell"/>: false means the chunk is not loaded, and
    /// an unloaded cell must never read as air.
    /// </summary>
    private bool TryGetCell(int x, int y, int z, out byte id, out byte state)
    {
        if (!TryGetChunk(x, y, z, out var chunk))
        {
            id = 0;
            state = 0;
            return false;
        }

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        id = chunk!.GetLocalBlock(lx, ly, lz);
        state = chunk.GetLocalState(lx, ly, lz);
        return true;
    }

    /// <summary>
    /// Writes a cell's id and state with no remesh of its own, queueing the chunks whose mesh
    /// the change shows up in for the batch at the end of the tick. Fluids pass light and cast
    /// no ambient occlusion, so only the cell's own chunk and the chunks across a border it
    /// touches are affected — never the whole light radius.
    /// </summary>
    private void Write(int x, int y, int z, byte id, byte state)
    {
        if (!TryGetChunk(x, y, z, out var chunk)) return;

        chunk!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        AddTouchedChunks(x, y, z, _dirtyChunks);
    }

    /// <summary>Whether fluid may replace what is in a cell: loaded, not already fluid, and
    /// either air or a fragile block the flow washes away.</summary>
    private bool CanFlowInto(int x, int y, int z)
    {
        return TryGetCell(x, y, z, out var id, out _) && IsFlowTarget(id);
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
    private bool IsWaterHole(int x, int y, int z, byte fluid)
    {
        return TryGetCell(x, y, z, out var id, out _)
               && (BlockType.IsReplaceable(id) || id == fluid);
    }

    /// <summary>Whether the slope search may travel through a cell: replaceable, or the same
    /// fluid but not a source (a source is a destination, not a corridor).</summary>
    private bool CanPassThrough(int x, int y, int z, byte fluid)
    {
        if (!TryGetCell(x, y, z, out var id, out var state)) return false;
        return BlockType.IsReplaceable(id) || (id == fluid && !FluidState.IsSource(state));
    }

    /// <summary>Whether any of the six face neighbours of a cell holds the given fluid.
    /// Unloaded neighbours read as absent, never as contact.</summary>
    private bool TouchesFluid(int x, int y, int z, byte fluidId)
    {
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (TryGetCell(x + dx, y + dy, z + dz, out var id, out _) && id == fluidId)
                return true;
        }

        return (TryGetCell(x, y + 1, z, out var above, out _) && above == fluidId)
               || (TryGetCell(x, y - 1, z, out var below, out _) && below == fluidId);
    }

    /// <summary>
    /// Block id a flow of <paramref name="fluid"/> settling at a cell hardens into when a
    /// neighbouring fluid hardens on its touch (water reaching lava crusts to the lava's
    /// cobblestone), or 0 when no neighbour does. Unloaded neighbours harden nothing.
    /// </summary>
    private byte HardenedByNeighbour(int x, int y, int z, byte fluid)
    {
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (TryGetCell(x + dx, y + dy, z + dz, out var id, out _)
                && BlockType.IsFluid(id)
                && BlockType.HardensTouchingById[id] == fluid
                && BlockType.HardensToById[id] != 0)
                return BlockType.HardensToById[id];
        }

        if (TryGetCell(x, y + 1, z, out var above, out _)
            && BlockType.IsFluid(above)
            && BlockType.HardensTouchingById[above] == fluid
            && BlockType.HardensToById[above] != 0)
            return BlockType.HardensToById[above];

        if (TryGetCell(x, y - 1, z, out var below, out _)
            && BlockType.IsFluid(below)
            && BlockType.HardensTouchingById[below] == fluid
            && BlockType.HardensToById[below] != 0)
            return BlockType.HardensToById[below];

        return 0;
    }

    /// <summary>Horizontally adjacent source cells of the same fluid.</summary>
    private int SourceNeighbourCount(int x, int y, int z, byte fluid)
    {
        var count = 0;
        foreach (var (dx, dy, dz) in Horizontals)
        {
            if (TryGetCell(x + dx, y + dy, z + dz, out var id, out var state)
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
    private void TryConvertMudAround(int x, int y, int z, byte fluid)
    {
        if (!MudIds(out var dirt, out var mud, out var water)) return;
        if (fluid != water) return;

        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (!TryGetCell(nx, ny, nz, out var id, out _) || id != dirt) continue;
            if (!IsMudWet(nx, ny, nz, water)) continue;

            Write(nx, ny, nz, mud, 0);
            ScheduleAround(nx, ny, nz);
        }
    }

    /// <summary>
    /// Whether a dirt cell counts as submerged: a water cell directly above it, and enough
    /// water across the whole 26-neighbourhood. Unloaded neighbours read as absent.
    /// </summary>
    private bool IsMudWet(int x, int y, int z, byte water)
    {
        if (!TryGetCell(x, y + 1, z, out var above, out _) || above != water) return false;

        var count = 0;
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            if (TryGetCell(x + dx, y + dy, z + dz, out var id, out _) && id == water
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
    /// the cell sits on a chunk border, to <paramref name="into"/> as packed chunk keys.</summary>
    private static void AddTouchedChunks(int x, int y, int z, HashSet<long> into)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        into.Add(Pack(cx, cy, cz));

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;

        if (lx == 0) into.Add(Pack(cx - 1, cy, cz));
        else if (lx == Chunk.SizeMask) into.Add(Pack(cx + 1, cy, cz));

        if (ly == 0) into.Add(Pack(cx, cy - 1, cz));
        else if (ly == Chunk.SizeMask) into.Add(Pack(cx, cy + 1, cz));

        if (lz == 0) into.Add(Pack(cx, cy, cz - 1));
        else if (lz == Chunk.SizeMask) into.Add(Pack(cx, cy, cz + 1));
    }

    private static Vector3 ChunkPosOf(int x, int y, int z) =>
        new(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    private static long Pack(int x, int y, int z) =>
        ((x & PackMask) << (2 * PackBits)) | ((y & PackMask) << PackBits) | (z & PackMask);

    private static void Unpack(long key, out int x, out int y, out int z)
    {
        x = SignExtend((int)((key >> (2 * PackBits)) & PackMask));
        y = UnpackedY(key);
        z = SignExtend((int)(key & PackMask));
    }

    // A packed key's Y, for ordering an over-budget tick lowest-cell-first without a full unpack
    private static int UnpackedY(long key) => SignExtend((int)((key >> PackBits) & PackMask));

    // A packed axis keeps only the low PackBits, so its sign bit has to be pushed back up
    private static int SignExtend(int value) => (value << (32 - PackBits)) >> (32 - PackBits);
}
