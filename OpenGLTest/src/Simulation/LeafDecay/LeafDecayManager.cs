using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;
using VoxelEngine.Lod;
using VoxelEngine.Particles;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Simulation.LeafDecay;

/// <summary>
/// Leaf decay and orphaned-wood crumbling: a fixed tick
/// (<see cref="SimulationConfig.LeafDecayTickSeconds"/>) over a set of scheduled cells, in
/// the same shape as <see cref="FluidManager"/>. Only scheduled cells do any work, and only a
/// crumbled wood cell reschedules its neighborhood (a decayed leaf feeds nothing), so a settled
/// canopy costs nothing.
/// Every edit path that can change a leaf's support (mining, felling, placement, a blast, a
/// bucket) calls <see cref="ScheduleAround(Vector3)"/> or <see cref="ScheduleBox"/>; both grow
/// what they are given by <see cref="ScheduleReach"/>, so callers never need to know a
/// species' leaf radius.
///
/// <para><b>Hydration.</b> A scheduled, non-persistent leaf re-derives its hydration with the
/// exact rule worldgen stamped it with: the best <see cref="LeafCluster.HydrationFromTip(TreeSpecies, int, int, int, int)"/> over
/// every live tip of its species whose cluster shape holds the cell, capped at
/// <see cref="LeafState.MaxHydration"/>. The cluster's radius follows the tree's support
/// (<see cref="LeafCluster.ClusterRadius(TreeSpecies, int)"/>): the world re-derives it as the
/// <see cref="WoodState.DiameterPx"/> of the root cell the tip's parent chain ends at, the very
/// number <see cref="LeafCluster.SupportPx"/> reads off the graph. A live tip in the world is a non-detached cell of the
/// species' wood block no thicker than its <c>tipMaxPx</c> that no face neighbor of the same
/// wood names as its parent (it has no child). Because this reproduces the stamped value bit for
/// bit, an intact canopy re-derives to exactly what it holds and is never rewritten, and a leaf
/// held by another live tip of the same species survives by construction. (The world cannot
/// tell a root-walk feature node from a branch, so a thin childless root counts as a tip here
/// where the graph rule skips it; that can only raise a leaf's hydration, never decay it.) A chain rule
/// ("an adjacent leaf minus one") was rejected: a leaf reachable from its tip only through the
/// branch's own wood, behind a bend, would re-derive to 0 on an intact tree. A leaf whose
/// hydration falls to 0 decays to air, rolling its loot through
/// <see cref="DropManager.SpawnLoot"/>; at most
/// <see cref="SimulationConfig.LeafDecayMaxRemovalsPerTick"/> cells are removed per tick,
/// and a tick's cells are visited in a hash-scattered order, so a felled crown dissolves in
/// speckles over a few seconds instead of sweeping in scan order or vanishing in one frame.
/// Persistent leaves (player-placed) are never touched.</para>
///
/// <para><b>Orphaned wood.</b> A scheduled tree-wood cell that has a parent cell (not a root,
/// not detached; shells included) crumbles when it has lost every path to an anchor: exactly
/// <see cref="TreeSupport"/>'s rule, the one <see cref="TopplingTree.Collect"/> fells by, so the
/// crumble takes what a fell at the same cut would drop and nothing else (paths over parent edges
/// plus vertical shell-to-shell adjacency; anchors are roots and shells standing on real ground,
/// so a wide trunk with one shell of a slice still standing never crumbles). A cell first walks
/// its own parent chain, which on an intact tree reaches an anchor in about its height; only a
/// broken chain pays for the full support search. Both answers are memoized for the frame
/// (cleared at the top of every <see cref="Update"/>): inside a frame only decay removals edit
/// the world, and removing a cell of an unsupported component never changes whether another
/// component is anchored, so later cells of the same trunk stop after a step or two. Crumbling
/// runs under the same budget, dropping the cell's thickness-band loot through the (id, state)
/// loot path, and each crumbled cell schedules its own reach box, so a blasted subtree crumbles
/// from the cut outward one layer per tick. Mining-driven felling stays
/// <see cref="TopplingTree"/>'s job.</para>
///
/// <para><b>Freezing.</b> Cells are read through <see cref="LeafDecayCells.TryGetCell"/>, never
/// <c>GetBlockGlobal</c>, which reports unloaded chunks as air. Whenever a decision needs a cell
/// in an unloaded chunk — the cell itself, any candidate tip or a tip's neighbor in a leaf's
/// cluster box, or any cell an orphan candidate's support walk or search reads — the scheduled cell is dropped silently and
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

    // Canopy columns (packed (x, 0, z)) that lost a tree block this frame, flushed to the far
    // LOD once per frame so a dissolving crown marks each column once
    private readonly HashSet<long> _editedColumns = [];

    // The species owning each leaf block and each wood block, by id
    private readonly TreeSpecies?[] _speciesByLeaf = new TreeSpecies?[256];
    private readonly TreeSpecies?[] _speciesByWood = new TreeSpecies?[256];

    // Set by Update before the ticks run
    private ChunkManager _chunks = null!;
    private DropManager _drops = null!;
    private LeafParticleSystem? _leaves;

    // The components: cell access for the frame, the orphaned-wood test and the leaf hydration rule
    private readonly LeafDecayCells _cells = new();
    private readonly OrphanWoodCheck _orphans;
    private readonly TipHydration _hydration;

    /// <summary>
    /// How far around an edit cells are scheduled: the largest leaf radius of any leafy species
    /// plus one, the extra cell covering a parent that an edit turns into a tip (its own leaves
    /// lie within the radius of it) and the children orphaned by a removed cell.
    /// </summary>
    public int ScheduleReach { get; }

    public LeafDecayManager()
    {
        _orphans = new OrphanWoodCheck(_cells);
        _hydration = new TipHydration(_cells);

        var maxRadius = 0;
        foreach (var species in TreeSpeciesRegistry.All)
        {
            _speciesByWood[species.WoodId] = species;
            if (!species.HasLeaves) continue;
            _speciesByLeaf[species.LeavesId] = species;
            _hydration.AddSpecies(species);
            maxRadius = Math.Max(maxRadius, species.LeafRadius);
        }

        ScheduleReach = Math.Max(1, maxRadius + 1);
    }

    /// <summary>Test seam: cells waiting for a tick, scheduled or still in the current one.</summary>
    internal int PendingCells => _next.Count + (_tickCells.Count - _tickCursor);

    /// <summary>Test seam: how many ticks have started.</summary>
    internal int TickIndex => _tickIndex;

    /// <summary>Test seam: whether the current tick still has cells to visit.</summary>
    internal bool TickInProgress => _tickCursor < _tickCells.Count;

    /// <summary>
    /// False on a multiplayer client, which never ticks the decay (the server's broadcast carries
    /// it): scheduling then returns at once instead of filling a backlog nothing drains.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Schedules every cell within <see cref="ScheduleReach"/> of an edited cell.</summary>
    public void ScheduleAround(Vector3 cell) => ScheduleBox(cell, cell);

    /// <summary>Schedules every cell within <see cref="ScheduleReach"/> of an edited cell.</summary>
    public void ScheduleAround(int x, int y, int z) => ScheduleBox(new Vector3(x, y, z), new Vector3(x, y, z));

    /// <summary>
    /// Schedules every cell of the inclusive box grown by <see cref="ScheduleReach"/> on all
    /// sides. Stops adding once the backlog reaches
    /// <see cref="SimulationConfig.MaxLeafDecayBacklogCells"/>; a stale leaf left behind is
    /// picked up by the next nearby edit.
    /// </summary>
    public void ScheduleBox(Vector3 min, Vector3 max)
    {
        if (!Enabled) return;
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
            if (_next.Count >= SimulationConfig.MaxLeafDecayBacklogCells) return;
            _next.Add(LeafDecayCells.Pack(x, y, z));
        }
    }

    /// <summary>
    /// Advances the tick clock and runs whatever is due, in slices of
    /// <see cref="SimulationConfig.MaxLeafDecayCellsPerFrame"/> cells. Leftover time past
    /// the tick budget is dropped. Removals remesh and reach the far LOD once, at the end.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod = null,
        LeafParticleSystem? leaves = null)
    {
        _accumulator += deltaTime;

        _chunks = chunks;
        _drops = drops;
        _leaves = leaves;

        _cells.BeginFrame(chunks);

        // The support memos hold for one frame: only decay removals happen inside it, and
        // removing an unsupported cell never changes another component's anchoring
        _orphans.BeginFrame();
        _hydration.BeginFrame();

        var ticks = 0;
        var processed = 0;
        while (processed < SimulationConfig.MaxLeafDecayCellsPerFrame)
        {
            // The previous tick is fully processed, so start the next one when it is due
            if (_tickCursor >= _tickCells.Count)
            {
                if (_accumulator < SimulationConfig.LeafDecayTickSeconds
                    || ticks >= SimulationConfig.MaxLeafDecayTicksPerFrame)
                    break;

                _accumulator -= SimulationConfig.LeafDecayTickSeconds;
                ticks++;
                BeginTick();
                if (_tickCells.Count == 0) continue;
            }

            // Spend what is left of the frame's cell budget on the current tick, then yield
            var slice = Math.Min(SimulationConfig.MaxLeafDecayCellsPerFrame - processed,
                _tickCells.Count - _tickCursor);
            var end = _tickCursor + slice;
            for (; _tickCursor < end; _tickCursor++)
                TickCell(_tickCells[_tickCursor]);
            processed += slice;

            if (_tickCursor < _tickCells.Count) break;
        }

        if (_accumulator >= SimulationConfig.LeafDecayTickSeconds) _accumulator = 0f;

        _cells.FlushDirty(chunks);

        if (_editedColumns.Count > 0)
        {
            foreach (var key in _editedColumns)
            {
                LeafDecayCells.Unpack(key, out var x, out _, out var z);
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
            LeafDecayCells.Unpack(key, out var x, out var y, out var z);
            if (_cells.TryGetCell(x, y, z, out var id, out _) && IsDecayCandidate(id))
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
        LeafDecayCells.Unpack(key, out var x, out var y, out var z);

        // An unloaded cell is frozen: dropped silently, never decayed
        if (!_cells.TryGetCell(x, y, z, out var id, out var state)) return;

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
        if (!_hydration.TryHydrationAt(species, x, y, z, out var hydration)) return;

        if (hydration > 0)
        {
            if (hydration != LeafState.Hydration(state))
                _cells.Write(x, y, z, id, LeafState.WithHydration(state, hydration), remesh: false);
            return;
        }

        if (LeafState.Hydration(state) != 0)
        {
            state = LeafState.WithHydration(state, 0);
            _cells.Write(x, y, z, id, state, remesh: false);
        }

        if (_removalsThisTick >= SimulationConfig.LeafDecayMaxRemovalsPerTick)
        {
            _next.Add(key);
            return;
        }

        Remove(x, y, z, id, state);
    }

    /// <summary>
    /// Crumbles a tree-wood cell that has lost every path to an anchor (<see cref="TreeSupport"/>'s
    /// rule, shared with <see cref="TopplingTree.Collect"/>). Roots and detached (player-placed)
    /// cells are exempt; shells are not. Any read that hits an unloaded chunk freezes the cell.
    /// </summary>
    private void TickWood(int x, int y, int z, byte id, byte state, long key)
    {
        if (!WoodState.HasParentCell(state) || WoodState.IsDetached(state)) return;

        if (!_orphans.IsOrphaned(x, y, z, id, state)) return;

        if (_removalsThisTick >= SimulationConfig.LeafDecayMaxRemovalsPerTick)
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
        _cells.Write(x, y, z, 0, 0, remesh: true);
        _removalsThisTick++;

        _chunks.UnmarkPlayerPlaced(x, y, z);
        var cell = new Vector3(x, y, z);
        _drops.SpawnLoot(id, cell, state: state);
        PopUnsupportedAround(cell);

        if (TreeGenerator.IsTreeBlock(id)) _editedColumns.Add(LeafDecayCells.Pack(x, 0, z));

        // A leaf feeds nothing: no other leaf's hydration and no wood's support reads it, so a
        // decayed leaf schedules nothing. A reach box per decayed leaf would flood the backlog
        // while a crown dissolves and crowd out the boxes real edits schedule.
        if (_speciesByLeaf[id] != null)
        {
            _leaves?.Burst(cell, ParticleConfig.LeafParticleBurstDecay);
            return;
        }

        ScheduleAround(x, y, z);
    }

    /// <summary>
    /// Pops, with its loot, every support-fragile block the removed cell <paramref name="holder"/>
    /// held up (see <see cref="BlockSupport"/>): a torch hanging on a decayed leaf, or a pebble on
    /// crumbled wood, would otherwise float. A popped block may emit light (a torch), so the light
    /// radius around it rebuilds, not just its own chunk. An unloaded neighbor is left alone.
    /// </summary>
    private void PopUnsupportedAround(Vector3 holder)
    {
        for (var face = 0; face < 6; face++)
        {
            var cell = holder + Chunk.CubeFaces[face].normal;
            int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
            if (!_cells.TryGetCell(x, y, z, out var id, out var state) || id == 0) continue;
            if (!BlockSupport.IsHeldUpBy(id, state, cell, holder)) continue;

            _cells.Write(x, y, z, 0, 0, remesh: true);
            _chunks.UnmarkPlayerPlaced(x, y, z);
            _chunks.MarkAreaDirty(cell, cell + Vector3.One, LightingConfig.LightPropagationRadius);
            _drops.SpawnLoot(id, cell,
                DropManager.PileCount(id, state), DropManager.PileSecondary(id, state), state);
        }
    }
}
