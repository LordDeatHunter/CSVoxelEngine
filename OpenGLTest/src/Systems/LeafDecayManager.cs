using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Leaf decay and orphaned-wood crumbling: a fixed tick
/// (<see cref="WorldGenerationConfig.LeafDecayTickSeconds"/>) over a set of scheduled cells, in
/// the same shape as <see cref="FluidManager"/>. Only scheduled cells do any work, and only a
/// crumbled wood cell reschedules its neighbourhood (a decayed leaf feeds nothing), so a settled
/// canopy costs nothing.
/// Every edit path that can change a leaf's support (mining, felling, placement, a blast, a
/// bucket) calls <see cref="ScheduleAround(Vector3)"/> or <see cref="ScheduleBox"/>; both grow
/// what they are given by <see cref="ScheduleReach"/>, so callers never need to know a
/// species' leaf radius.
///
/// <para><b>Hydration.</b> A scheduled, non-persistent leaf re-derives its hydration with the
/// exact rule worldgen stamped it with: the best <see cref="LeafCluster.HydrationFromTip"/> over
/// every live tip of its species whose cluster shape holds the cell, capped at
/// <see cref="LeafState.MaxHydration"/>. A live tip in the world is a non-detached cell of the
/// species' wood block no thicker than its <c>tipMaxPx</c> that no face neighbour of the same
/// wood names as its parent (it has no child). Because this reproduces the stamped value bit for
/// bit, an intact canopy re-derives to exactly what it holds and is never rewritten, and a leaf
/// held by another live tip of the same species survives by construction. (The world cannot
/// tell a root-walk feature node from a branch, so a thin childless root counts as a tip here
/// where the graph rule skips it; that can only raise a leaf's hydration, never decay it.) A chain rule
/// ("an adjacent leaf minus one") was rejected: a leaf reachable from its tip only through the
/// branch's own wood, behind a bend, would re-derive to 0 on an intact tree. A leaf whose
/// hydration falls to 0 decays to air, rolling its loot through
/// <see cref="DropManager.SpawnLoot"/>; at most
/// <see cref="WorldGenerationConfig.LeafDecayMaxRemovalsPerTick"/> cells are removed per tick,
/// and a tick's cells are visited in a hash-scattered order, so a felled crown dissolves in
/// speckles over a few seconds instead of sweeping in scan order or vanishing in one frame.
/// Persistent leaves (player-placed) are never touched.</para>
///
/// <para><b>Orphaned wood.</b> A scheduled tree-wood cell that has a parent cell (not a root,
/// not detached, not a phase-7 shell) whose loaded parent no longer
/// <see cref="TreeWoodShape.Connects"/> to it crumbles too, under the same budget, dropping its
/// thickness-band loot through the (id, state) loot path. Each crumbled cell schedules its own
/// reach box, so a blasted subtree crumbles from the cut outward one layer per tick. Mining-driven
/// felling stays <see cref="TopplingTree"/>'s job.</para>
///
/// <para><b>Freezing.</b> Cells are read through <see cref="TryGetCell"/>, never
/// <c>GetBlockGlobal</c>, which reports unloaded chunks as air. Whenever a decision needs a cell
/// in an unloaded chunk — the cell itself, any candidate tip or a tip's neighbour in a leaf's
/// cluster box, or an orphan candidate's parent — the scheduled cell is dropped silently and
/// never decayed; the next nearby edit reschedules it.</para>
///
/// <para><b>Remeshing.</b> A hydration rewrite remeshes nothing: the mesher never reads
/// <see cref="LeafState"/>. A removal remeshes only the cell's own chunk and the chunks across
/// a border it touches, batched once per frame through <see cref="ChunkManager.MarkDirty"/>:
/// leaves and tree wood both pass light, cast no ambient occlusion and occlude nothing, so the
/// light radius around them never changes.</para>
///
/// <para><b>Far LOD.</b> A removed canopy block poisons its column's canopy marker with radius
/// 0 and marks the column edited on the <see cref="LodManager"/>, deduplicated per column per
/// frame. Tree blocks are never surface solids, so no <c>LodSurface</c> override is written.</para>
///
/// <para><b>Chunk loads schedule nothing.</b> Worldgen leaves are consistent by construction.
/// A restored chunk can hold leaves whose wood was cut before it was saved; those wait for the
/// next nearby edit, since walking every restored chunk for leaves would cost every load.</para>
///
/// Main thread only: it writes block data and schedules remeshes.
/// </summary>
public class LeafDecayManager
{
    // Cell keys pack a world position into 21 bits per axis (±1,048,576 blocks), as in
    // FluidManager.
    private const int PackBits = 21;
    private const long PackMask = (1L << PackBits) - 1;

    private float _accumulator;

    // How many ticks have started; a test seam for counting ticks
    private int _tickIndex;

    // Cells removed in the current tick; once it reaches the budget the rest re-queue
    private int _removalsThisTick;

    // Cells scheduled since the last tick started; a tick snapshots it into _tickCells, so a
    // cell scheduled while ticking is handled on the following tick
    private readonly HashSet<long> _next = [];

    // The current tick's cells, walked one per-frame slice at a time
    private readonly List<long> _tickCells = [];
    private int _tickCursor;

    // Chunks touched by this frame's removals, remeshed once the frame's slice is done
    private readonly HashSet<long> _dirtyChunks = [];

    // Canopy columns (packed (x, 0, z)) that lost a tree block this frame, flushed to the far
    // LOD once per frame so a dissolving crown marks each column once
    private readonly HashSet<long> _editedColumns = [];

    // The species owning each leaf block and each wood block, by id
    private readonly TreeSpecies?[] _speciesByLeaf = new TreeSpecies?[256];
    private readonly TreeSpecies?[] _speciesByWood = new TreeSpecies?[256];

    // Per leaf id, every offset from a tip to a cell that tip hydrates (h >= 1), sorted by
    // hydration descending: the first live tip a scan meets gives the cell its best hydration,
    // so an intact leaf usually stops after a candidate or two
    private readonly TipOffset[]?[] _offsetsByLeaf = new TipOffset[]?[256];

    private readonly record struct TipOffset(int Dx, int Dy, int Dz, int Hydration);

    // Set by Update before the ticks run
    private ChunkManager _chunks = null!;
    private DropManager _drops = null!;
    private LodManager? _lod;
    private LeafParticleSystem? _leaves;

    // Direct-mapped chunk-lookup cache for one frame, as in FluidManager: keys cleared at the
    // top of every Update, only the Chunk cached (never its arrays, which SetBlockRaw may swap)
    private const int ChunkCacheSize = 16;
    private const int ChunkCacheMask = ChunkCacheSize - 1;
    private readonly Chunk?[] _chunkCache = new Chunk?[ChunkCacheSize];
    private readonly long[] _chunkCacheKeys = new long[ChunkCacheSize];

    /// <summary>
    /// How far around an edit cells are scheduled: the largest leaf radius of any leafy species
    /// plus one, the extra cell covering a parent that an edit turns into a tip (its own leaves
    /// lie within the radius of it) and the children orphaned by a removed cell.
    /// </summary>
    public int ScheduleReach { get; }

    public LeafDecayManager()
    {
        var maxRadius = 0;
        foreach (var species in TreeSpeciesRegistry.All)
        {
            _speciesByWood[species.WoodId] = species;
            if (!species.HasLeaves) continue;
            _speciesByLeaf[species.LeavesId] = species;
            _offsetsByLeaf[species.LeavesId] = TipOffsets(species);
            maxRadius = Math.Max(maxRadius, species.LeafRadius);
        }

        ScheduleReach = Math.Max(1, maxRadius + 1);
    }

    // Every offset a tip of the species hydrates, best first (a leaf is never its own tip);
    // OrderByDescending is stable, so ties keep the fixed x, y, z scan order
    private static TipOffset[] TipOffsets(TreeSpecies species)
    {
        var radius = species.LeafRadius;
        var offsets = new List<TipOffset>();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dy = -radius; dy <= radius; dy++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var h = LeafCluster.HydrationFromTip(species, dx, dy, dz);
            if (h >= 1) offsets.Add(new TipOffset(dx, dy, dz, h));
        }

        return offsets.OrderByDescending(o => o.Hydration).ToArray();
    }

    /// <summary>Test seam: cells waiting for a tick, scheduled or still in the current one.</summary>
    internal int PendingCells => _next.Count + (_tickCells.Count - _tickCursor);

    /// <summary>Test seam: how many ticks have started.</summary>
    internal int TickIndex => _tickIndex;

    /// <summary>Test seam: whether the current tick still has cells to visit.</summary>
    internal bool TickInProgress => _tickCursor < _tickCells.Count;

    /// <summary>Schedules every cell within <see cref="ScheduleReach"/> of an edited cell.</summary>
    public void ScheduleAround(Vector3 cell) => ScheduleBox(cell, cell);

    /// <summary>Schedules every cell within <see cref="ScheduleReach"/> of an edited cell.</summary>
    public void ScheduleAround(int x, int y, int z) => ScheduleBox(new Vector3(x, y, z), new Vector3(x, y, z));

    /// <summary>
    /// Schedules every cell of the inclusive box grown by <see cref="ScheduleReach"/> on all
    /// sides. Stops adding once the backlog reaches
    /// <see cref="WorldGenerationConfig.MaxLeafDecayBacklogCells"/>; a stale leaf left behind is
    /// picked up by the next nearby edit.
    /// </summary>
    public void ScheduleBox(Vector3 min, Vector3 max)
    {
        var reach = ScheduleReach;
        var x0 = (int)MathF.Floor(min.X) - reach;
        var y0 = (int)MathF.Floor(min.Y) - reach;
        var z0 = (int)MathF.Floor(min.Z) - reach;
        var x1 = (int)MathF.Floor(max.X) + reach;
        var y1 = (int)MathF.Floor(max.Y) + reach;
        var z1 = (int)MathF.Floor(max.Z) + reach;

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
        {
            if (_next.Count >= WorldGenerationConfig.MaxLeafDecayBacklogCells) return;
            _next.Add(Pack(x, y, z));
        }
    }

    /// <summary>
    /// Advances the tick clock and runs whatever is due, in slices of
    /// <see cref="WorldGenerationConfig.MaxLeafDecayCellsPerFrame"/> cells. Leftover time past
    /// the tick budget is dropped. Removals remesh and reach the far LOD once, at the end.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod = null,
        LeafParticleSystem? leaves = null)
    {
        _accumulator += deltaTime;

        _chunks = chunks;
        _drops = drops;
        _lod = lod;
        _leaves = leaves;

        // The chunk cache is valid for one frame only: an unload can replace a chunk between
        // frames, and a reference cached before it must never be handed back
        _chunkCacheKeys.AsSpan().Fill(-1L);

        var ticks = 0;
        var processed = 0;
        while (processed < WorldGenerationConfig.MaxLeafDecayCellsPerFrame)
        {
            // The previous tick is fully processed, so start the next one when it is due
            if (_tickCursor >= _tickCells.Count)
            {
                if (_accumulator < WorldGenerationConfig.LeafDecayTickSeconds
                    || ticks >= WorldGenerationConfig.MaxLeafDecayTicksPerFrame)
                    break;

                _accumulator -= WorldGenerationConfig.LeafDecayTickSeconds;
                ticks++;
                BeginTick();
                if (_tickCells.Count == 0) continue;
            }

            // Spend what is left of the frame's cell budget on the current tick, then yield
            var slice = Math.Min(WorldGenerationConfig.MaxLeafDecayCellsPerFrame - processed,
                _tickCells.Count - _tickCursor);
            var end = _tickCursor + slice;
            for (; _tickCursor < end; _tickCursor++)
                TickCell(_tickCells[_tickCursor]);
            processed += slice;

            if (_tickCursor < _tickCells.Count) break;
        }

        if (_accumulator >= WorldGenerationConfig.LeafDecayTickSeconds) _accumulator = 0f;

        if (_dirtyChunks.Count > 0)
        {
            foreach (var key in _dirtyChunks)
            {
                Unpack(key, out var cx, out var cy, out var cz);
                chunks.MarkDirty(new Vector3(cx, cy, cz));
            }
            _dirtyChunks.Clear();
        }

        if (_editedColumns.Count > 0)
        {
            foreach (var key in _editedColumns)
            {
                Unpack(key, out var x, out _, out var z);
                TreeGenerator.PoisonCanopy(x, z, 0);
                lod?.MarkEditedColumn(x, z);
            }
            _editedColumns.Clear();
        }
    }

    /// <summary>
    /// Starts a new tick: snapshots the scheduled cells that hold a leaf or tree wood and orders
    /// them by hash, so a budget-limited tick removes a scattered subset rather than a sweep in
    /// scan order.
    /// </summary>
    private void BeginTick()
    {
        _tickIndex++;
        _removalsThisTick = 0;

        // Air and terrain fill most of a scheduled box (a whole fell's is tens of thousands of
        // cells) and have nothing to re-derive: one read here drops them, so the per-frame cell
        // budget and the sort only ever see the cells that do work. An unloaded cell is frozen
        // and dropped here as well, exactly as TickCell would drop it.
        _tickCells.Clear();
        foreach (var key in _next)
        {
            Unpack(key, out var x, out var y, out var z);
            if (TryGetCell(x, y, z, out var id, out _) && IsDecayCandidate(id))
                _tickCells.Add(key);
        }

        _next.Clear();
        _tickCursor = 0;
        _tickCells.Sort(ScatterOrder);
    }

    // A leaf of a leafy species, or the wood block of any species
    private bool IsDecayCandidate(byte id) =>
        _speciesByLeaf[id] != null || (BlockType.IsTreeWood(id) && _speciesByWood[id] != null);

    private static int ScatterOrder(long a, long b) =>
        SplitMix64.Mix((ulong)a).CompareTo(SplitMix64.Mix((ulong)b));

    /// <summary>Re-derives one scheduled cell: a leaf's hydration or a wood cell's support.</summary>
    private void TickCell(long key)
    {
        Unpack(key, out var x, out var y, out var z);

        // An unloaded cell is frozen: dropped silently, never decayed
        if (!TryGetCell(x, y, z, out var id, out var state)) return;

        if (_speciesByLeaf[id] is { } leafSpecies)
            TickLeaf(leafSpecies, x, y, z, id, state, key);
        else if (IsDecayCandidate(id))
            TickWood(x, y, z, id, state, key);
    }

    /// <summary>
    /// Re-derives a leaf's hydration from the live tips around it, rewrites it when it changed,
    /// and decays the leaf when nothing holds it any more.
    /// </summary>
    private void TickLeaf(TreeSpecies species, int x, int y, int z, byte id, byte state, long key)
    {
        if (LeafState.IsPersistent(state)) return;

        // A read in the cluster box hit an unloaded chunk: freeze rather than decide blind
        if (!TryHydrationAt(species, x, y, z, out var hydration)) return;

        if (hydration > 0)
        {
            if (hydration != LeafState.Hydration(state))
                Write(x, y, z, id, LeafState.WithHydration(state, hydration), remesh: false);
            return;
        }

        if (LeafState.Hydration(state) != 0)
        {
            state = LeafState.WithHydration(state, 0);
            Write(x, y, z, id, state, remesh: false);
        }

        if (_removalsThisTick >= WorldGenerationConfig.LeafDecayMaxRemovalsPerTick)
        {
            _next.Add(key);
            return;
        }

        Remove(x, y, z, id, state);
    }

    /// <summary>
    /// The exact hydration rule on world cells: the best per-tip hydration over every live tip
    /// of the species whose cluster holds (x, y, z), capped at <see cref="LeafState.MaxHydration"/>.
    /// False when a read hit an unloaded chunk.
    /// </summary>
    private bool TryHydrationAt(TreeSpecies species, int x, int y, int z, out int hydration)
    {
        hydration = 0;
        var offsets = _offsetsByLeaf[species.LeavesId]!;

        // Best hydration first, so the first live tip met gives the maximum. A read that hits
        // an unloaded chunk before that freezes the cell; one past it cannot change the answer
        for (var i = 0; i < offsets.Length; i++)
        {
            var (dx, dy, dz, h) = offsets[i];

            // The offset runs from the tip to the cell, so the candidate tip sits behind it
            var tx = x - dx;
            var ty = y - dy;
            var tz = z - dz;
            if (!TryGetCell(tx, ty, tz, out var tid, out var tstate)) return false;
            if (tid != species.WoodId) continue;
            if (!IsLiveTip(species, tx, ty, tz, tstate, out var unloaded))
            {
                if (unloaded) return false;
                continue;
            }

            hydration = Math.Min(h, LeafState.MaxHydration);
            return true;
        }

        return true;
    }

    /// <summary>
    /// Whether a cell of the species' wood is a live tip: not detached, no thicker than the
    /// species' tip limit, and no face neighbour of the same wood naming it as its parent.
    /// <paramref name="unloaded"/> reports a neighbour read that hit an unloaded chunk.
    /// </summary>
    private bool IsLiveTip(TreeSpecies species, int tx, int ty, int tz, byte tstate, out bool unloaded)
    {
        unloaded = false;
        if (WoodState.IsDetached(tstate) || WoodState.DiameterPx(tstate) > species.TipMaxPx) return false;

        for (var dir = 0; dir < TreeDirections.Count; dir++)
        {
            var nx = tx + TreeDirections.Dx[dir];
            var ny = ty + TreeDirections.Dy[dir];
            var nz = tz + TreeDirections.Dz[dir];
            if (!TryGetCell(nx, ny, nz, out var nid, out var nstate))
            {
                unloaded = true;
                return false;
            }

            // A child: the neighbour's parent points back at the candidate tip
            if (nid == species.WoodId && !WoodState.IsDetached(nstate)
                && WoodState.TryParentOffset(nstate, out var px, out var py, out var pz)
                && nx + px == tx && ny + py == ty && nz + pz == tz)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Crumbles a tree-wood cell whose loaded parent no longer connects to it. Roots, detached
    /// (player-placed) cells and shells are exempt; an unloaded parent freezes the cell.
    /// </summary>
    private void TickWood(int x, int y, int z, byte id, byte state, long key)
    {
        if (!WoodState.HasParentCell(state) || WoodState.IsShell(state)) return;

        var dir = WoodState.Parent(state);
        WoodState.TryParentOffset(state, out var px, out var py, out var pz);
        if (!TryGetCell(x + px, y + py, z + pz, out var pid, out var pstate)) return;
        if (TreeWoodShape.Connects(id, state, pid, pstate, dir)) return;

        if (_removalsThisTick >= WorldGenerationConfig.LeafDecayMaxRemovalsPerTick)
        {
            _next.Add(key);
            return;
        }

        Remove(x, y, z, id, state);
    }

    /// <summary>
    /// Turns a decayed leaf or an orphaned wood cell into air: remeshes, rolls its loot and
    /// reaches the far LOD. A leaf shakes a few leaves loose; a wood cell schedules its reach
    /// box, which covers the removed tip's leaves, a parent that turned into a tip and the
    /// children it orphaned.
    /// </summary>
    private void Remove(int x, int y, int z, byte id, byte state)
    {
        Write(x, y, z, 0, 0, remesh: true);
        _removalsThisTick++;

        _chunks.UnmarkPlayerPlaced(x, y, z);
        var cell = new Vector3(x, y, z);
        _drops.SpawnLoot(id, cell, state: state);

        if (TreeGenerator.IsTreeBlock(id)) _editedColumns.Add(Pack(x, 0, z));

        // A leaf feeds nothing: no other leaf's hydration and no wood's support reads it, so a
        // decayed leaf schedules nothing. A reach box per decayed leaf would flood the backlog
        // while a crown dissolves and crowd out the boxes real edits schedule.
        if (_speciesByLeaf[id] != null)
        {
            _leaves?.Burst(cell, WorldGenerationConfig.LeafParticleBurstDecay);
            return;
        }

        ScheduleAround(x, y, z);
    }

    /// <summary>
    /// Writes a cell's id and state through the frame's chunk cache. A hydration rewrite passes
    /// <paramref name="remesh"/> false: the mesher never reads <see cref="LeafState"/>, so
    /// nothing visible changes. A removal queues only the cell's own chunk and the chunks across
    /// a border it touches, never the light radius: leaves and tree wood both pass light, cast
    /// no ambient occlusion and occlude nothing, so taking one out changes no other cell's light.
    /// </summary>
    private void Write(int x, int y, int z, byte id, byte state, bool remesh)
    {
        if (!TryGetChunk(x, y, z, out var chunk)) return;

        chunk!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        if (remesh) AddTouchedChunks(x, y, z, _dirtyChunks);
    }

    /// <summary>
    /// The loaded chunk owning a cell, through the frame's direct-mapped cache; the miss path
    /// goes through <see cref="ChunkManager.ChunkAt"/>, so a chunk without ready block data
    /// reads as unloaded.
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
    /// Reads a world cell's block id and state through the frame's chunk cache. False means the
    /// chunk is not loaded, and an unloaded cell must never read as air.
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
