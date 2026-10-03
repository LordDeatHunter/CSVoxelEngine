using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Reactive tree growth on the live world (TREES_PLAN phase 6): finds the tree rooted in a
/// column (or plants a sapling root on the column's ground when it holds none), rebuilds its
/// graph from the wood state bytes (<see cref="TreeGraph.FromWorld"/>),
/// runs <see cref="TreeGrower.GrowFurther"/> in a <see cref="WorldTreeSpace"/>, rasterises the
/// result once and writes the difference back. GL-free; main thread only, since it writes block
/// data and schedules remeshes.
///
/// <para><b>Write rules.</b> Wood goes first, then leaves, all through
/// <see cref="Chunk.SetBlockRaw"/> (never marked player-placed). An existing wood cell of the
/// tree is only ever re-coded for thickness, and the rebuilt graph's thickness floors
/// (<see cref="TreeGraph.Node.FloorPx"/>) mean a code only ever rises, so growth never thins a
/// tree. A new node is written only into air or a non-persistent leaf of the species that the
/// space still calls free. A new leaf goes only into air; an existing non-persistent leaf of the
/// species only ever has its hydration raised; persistent leaves and every other block are never
/// touched.</para>
///
/// <para><b>Shells</b> (a wide trunk's ring cells, TREES_PLAN phase 7) are written after the nodes,
/// parent-first, and only when their parent cell already holds attached full-cube wood of the
/// species. An existing shell is only ever re-coded. A missing one is filled only on the <b>new
/// ring</b>: outside the footprint its core had before the call (the
/// <see cref="TreeRasterizer.BlockRadius"/> of the core's rebuilt floor), or around a core that is
/// new this call, and then only into air or a non-persistent leaf of the species that the space
/// still calls free. A shell mined or cut inside the old ring therefore stays mined, like a mined
/// leaf. Shells are never tips and never regrow leaves.</para>
///
/// <para><b>Features</b> (roots, buttress fins) run only on the call that plants a sapling, as
/// worldgen runs them once at maturity; a later grow never re-runs them and never rewrites an
/// existing feature node.</para>
///
/// <para><b>Leaves regrow only around changed tips</b>: a live tip that is new this call, or whose
/// wood state the call rewrote, plus every original tip still in the world when the call widened
/// the tree's <see cref="LeafCluster.ClusterRadius(TreeSpecies, TreeGraph)"/> (it follows the root
/// diameter, so a thickening trunk widens the whole crown). Leaf cells outside every changed tip's
/// cluster are never written, so a hole mined in an untouched part of the crown stays mined. A written leaf carries the full
/// rasterised hydration (the best over every live tip), which is exactly what
/// <see cref="LeafDecayManager"/> re-derives.</para>
///
/// <para><b>Decay is the pruner.</b> Leaves an old tip fed lose hydration once that tip grows a
/// child; the writer never lowers or removes them itself but schedules the written box once with
/// <see cref="LeafDecayManager.ScheduleBox"/>, so the hydration rule stays single-sourced.</para>
///
/// <para><b>Remeshing.</b> Every mesh-affecting write marks its own chunk plus the face-adjacent
/// chunk on each axis where it sits on a border through <see cref="ChunkManager.MarkDirty"/>, the
/// <see cref="LeafDecayManager"/> idiom. <c>Chunk.RemeshAfterBatchEdit</c> is avoided: it dirties
/// the whole light radius around each touched chunk, and leaves and tree wood pass light, cast no
/// ambient occlusion and occlude nothing, so no other cell's light changes.</para>
///
/// <para><b>Hooks.</b> <see cref="FluidManager.ScheduleAround(int, int, int)"/> for every cell
/// whose id changed; one <see cref="LeafDecayManager.ScheduleBox"/> over the written box; per
/// deduplicated column with a mesh-affecting write, <see cref="TreeGenerator.PoisonCanopy"/> with
/// radius 0 and <see cref="LodManager.MarkEditedColumn"/>. That far-LOD hook only removes the
/// column's stale canopy bump; there is no path that adds the grown tree to the far LOD.</para>
///
/// <para><b>RNG.</b> Each call seeds its stream from <see cref="GrowSeed"/>: the tree's
/// <see cref="TreeGrower.SeedFor"/> seed (root cell, world seed, species), the rebuilt graph's
/// node count and this writer's call counter, so the same history on the same world grows the
/// same tree, while a repeated grow never replays the previous call's pulses. Pulses run with
/// <see cref="TreeGrower.TreeEnergyAt"/>, the energy a worldgen tree on that root grew with.</para>
/// </summary>
public sealed class TreeGrowthWriter
{
    // Salts the tree seed so a grow stream never coincides with the worldgen maturity stream.
    private const ulong GrowSalt = 0x6A09E667F3BCC909UL;

    // Grow calls so far; advances every call's stream. In memory only.
    private ulong _calls;

    /// <summary>
    /// The stream seed of one grow call: the tree's seed mixed with the rebuilt graph's node count
    /// and the writer's call index.
    /// </summary>
    internal static ulong GrowSeed(ulong treeSeed, int nodeCount, ulong call) =>
        SplitMix64.Mix(SplitMix64.Mix(treeSeed ^ GrowSalt) + SplitMix64.Mix(((ulong)(uint)nodeCount << 32) ^ call));

    /// <summary>
    /// Grows the tree of <paramref name="species"/> rooted in column (x, z) by up to
    /// <paramref name="steps"/> pulses (clamped to
    /// <see cref="WorldGenerationConfig.TreeGrowMaxPulsesPerCall"/>) and writes the result. The root
    /// is the first cell of the species' wood with the root parent code met scanning the column
    /// top-down over the species' reach window around <paramref name="yHint"/>. A column with no
    /// root plants a sapling on its ground instead (<see cref="TreeGrowthStatus.Planted"/>, or
    /// <see cref="TreeGrowthStatus.NoGround"/> when there is no legal ground or the cell above it
    /// cannot take the root). Any read in that window or the rebuild that hits an unloaded chunk
    /// refuses the call (<see cref="TreeGrowthStatus.NotGrowable"/>) before anything is written.
    /// </summary>
    /// <param name="reserved">Cells growth must not fill (World passes the falling blocks' cells).</param>
    public TreeGrowthResult Grow(ChunkManager chunks, TreeSpecies species, int x, int yHint, int z, int steps,
        int worldSeed, FluidManager? fluids = null, LeafDecayManager? leafDecay = null, LodManager? lod = null,
        Func<int, int, int, bool>? reserved = null)
    {
        var reach = TreeRasterizer.ReachOf(species);
        var top = yHint + reach.Up;
        var bottom = yHint - reach.Down - 1;

        // One top-down pass finds the root, and on the way the topmost ground cell a sapling
        // would stand on should the column hold no tree
        var rootY = int.MinValue;
        var groundY = int.MinValue;
        byte groundId = 0, groundState = 0;
        for (var y = top; y >= bottom; y--)
        {
            if (!chunks.TryGetCell(x, y, z, out var id, out var state))
                return Refused(TreeGrowthStatus.NotGrowable, x, yHint, z);
            if (id == species.WoodId && WoodState.IsRoot(state))
            {
                rootY = y;
                break;
            }
            if (groundY == int.MinValue && IsGroundCandidate(id, state))
            {
                groundY = y;
                groundId = id;
                groundState = state;
            }
        }

        if (rootY == int.MinValue)
            return Plant(chunks, species, x, z, groundY, groundId, groundState, yHint, steps, worldSeed,
                fluids, leafDecay, lod, reserved);

        var graph = TreeGraph.FromWorld(chunks.TryGetCell, species, x, rootY, z);
        if (graph == null)
            return Refused(TreeGrowthStatus.NotGrowable, x, rootY, z);

        return GrowAndWrite(chunks, species, graph, graph.Count, steps, worldSeed, fluids, leafDecay, lod, reserved,
            new WriteLog(), TreeGrowthStatus.Grew);
    }

    private static TreeGrowthResult Refused(TreeGrowthStatus status, int x, int y, int z) =>
        new(status, x, y, z, 0, 0, 0, 0);

    /// <summary>
    /// Plants a sapling root on the column's ground and grows it from there. The ground is the
    /// topmost cell of the window that is not air, leaves, attached tree wood or a
    /// placement-replaceable cover (those are looked through); it has to pass
    /// <see cref="IsSaplingGround"/> and the cell above it has to be
    /// <see cref="WorldTreeSpace.IsPlantable"/>, or nothing is written
    /// (<see cref="TreeGrowthStatus.NoGround"/>). A cover on the root cell is cleared first and
    /// drops nothing, as a placement displacing it does. The species' biome and ground-height
    /// rules are worldgen placement rules and are not applied.
    /// </summary>
    private TreeGrowthResult Plant(ChunkManager chunks, TreeSpecies species, int x, int z, int groundY,
        byte groundId, byte groundState, int yHint, int steps, int worldSeed, FluidManager? fluids,
        LeafDecayManager? leafDecay, LodManager? lod, Func<int, int, int, bool>? reserved)
    {
        if (groundY == int.MinValue || !IsSaplingGround(species, groundId, groundState))
            return Refused(TreeGrowthStatus.NoGround, x, yHint, z);

        var rootY = groundY + 1;
        var space = new WorldTreeSpace(chunks, species, reserved);
        if (!space.IsPlantable(x, rootY, z) || !chunks.TryGetCell(x, rootY, z, out var id, out var state))
            return Refused(TreeGrowthStatus.NoGround, x, rootY, z);

        var log = new WriteLog();
        if (id != 0 && !IsOwnGrowable(species, id, state))
            Write(chunks, x, rootY, z, 0, 0, log, idChanged: true, mesh: true);

        return GrowAndWrite(chunks, species, new TreeGraph(x, rootY, z, species.TipPx), 0, steps, worldSeed,
            fluids, leafDecay, lod, reserved, log, TreeGrowthStatus.Planted);
    }

    // A cell the ground scan stops at: anything but air, leaves, attached tree wood (a canopy or
    // another tree's branch over the column) and a cover a placement would take over
    private static bool IsGroundCandidate(byte id, byte state) =>
        id != 0
        && !TreeGenerator.IsLeaf(id)
        && !(BlockType.IsTreeWood(id) && !WoodState.IsDetached(state))
        && !BlockType.IsReplaceableByPlacement(id, state);

    /// <summary>
    /// Whether a sapling may root on this ground: a solid, non-fluid cell, and for a species whose
    /// worldgen needs soil (<see cref="TreeWorldgenRules.MinSoil"/> above 0) the surface or soil
    /// block.
    /// </summary>
    private static bool IsSaplingGround(TreeSpecies species, byte id, byte state)
    {
        if (!BlockType.IsSolidCell(id, state) || BlockType.IsFluid(id))
            return false;
        if (species.Worldgen?.MinSoil > 0)
            return id == TerrainGenerator.SurfaceBlock || id == TerrainGenerator.SoilBlock;
        return true;
    }

    /// <summary>
    /// The shared tail of every grow: grows <paramref name="graph"/> further, rasterises it once,
    /// writes wood then leaves under the write rules and fires the hooks. Nodes at or above
    /// <paramref name="originalCount"/> are new this call. <paramref name="log"/> may already hold
    /// writes made before growth (a cleared cover cell), which join the hooks.
    /// </summary>
    private TreeGrowthResult GrowAndWrite(ChunkManager chunks, TreeSpecies species, TreeGraph graph,
        int originalCount, int steps, int worldSeed, FluidManager? fluids, LeafDecayManager? leafDecay,
        LodManager? lod, Func<int, int, int, bool>? reserved, WriteLog log, TreeGrowthStatus status)
    {
        int rx = graph[TreeGraph.Root].X, ry = graph[TreeGraph.Root].Y, rz = graph[TreeGraph.Root].Z;

        var energy = TreeGrower.TreeEnergyAt(species, rx, ry, rz, worldSeed);
        var rng = new SplitMix64(GrowSeed(TreeGrower.SeedFor(rx, ry, rz, worldSeed, species), graph.Count, _calls++));
        var space = new WorldTreeSpace(chunks, species, reserved);

        // The block radius each original trunk-chain core already had (its shell ring before this
        // call), indexed by node: the shell pass refills nothing inside it. Thin species skip it.
        int[]? oldRadius = null;
        if (originalCount > 0 && TreeRasterizer.BlockRadius(species.MaxPx) > 0)
        {
            oldRadius = new int[originalCount];
            var chain = new List<int>();
            TreeRasterizer.TrunkChain(graph, chain);
            foreach (var t in chain)
                oldRadius[t] = TreeRasterizer.BlockRadius(graph[t].FloorPx);
        }

        // The tree's cluster radius before growth: FromWorld loads the root at its world
        // diameter, so this is the radius the leaves in the world were grown with
        var radiusBefore = species.HasLeaves ? LeafCluster.ClusterRadius(species, graph) : 0;

        var added = TreeGrower.GrowFurther(graph, species, space, rng, energy,
            Math.Clamp(steps, 0, WorldGenerationConfig.TreeGrowMaxPulsesPerCall), WorldGenerationConfig.TreeGrowNodeCap,
            applyFeatures: status == TreeGrowthStatus.Planted);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out var woodCount);

        // Wood pass: thickness rewrites of existing nodes, then new nodes, in index order (a
        // parent is always written before its children)
        var changed = new bool[graph.Count];
        var skipped = new bool[graph.Count];
        var woodWritten = 0;
        for (var i = 0; i < graph.Count; i++)
        {
            ref readonly var n = ref graph[i];
            // Existing feature nodes (roots, fins) are never rewritten; new ones (a planted
            // sapling's features) take the new-node path below
            if (n.IsFeature && i < originalCount)
                continue;
            var c = cells[i];

            // A node whose parent never reached the world must not float there either
            if (n.Parent >= 0 && skipped[n.Parent])
            {
                skipped[i] = true;
                continue;
            }

            if (!chunks.TryGetCell(c.X, c.Y, c.Z, out var id, out var state))
            {
                skipped[i] = true;
                continue;
            }

            if (id == species.WoodId && i < originalCount)
            {
                if (state != c.State)
                {
                    Write(chunks, c.X, c.Y, c.Z, c.Block, c.State, log, idChanged: false, mesh: true);
                    changed[i] = true;
                    woodWritten++;
                }
            }
            else if (i >= originalCount && IsOwnGrowable(species, id, state) && space.IsFree(c.X, c.Y, c.Z))
            {
                Write(chunks, c.X, c.Y, c.Z, c.Block, c.State, log, idChanged: true, mesh: true);
                changed[i] = true;
                woodWritten++;
            }
            else if (i >= originalCount)
            {
                // A guard: within one call a pulse only grows into cells the space calls free
                skipped[i] = true;
            }
        }

        // Shell pass: shells are emitted parent-first, so a parent written earlier in this pass is
        // already in the world when its children are read
        for (var i = graph.Count; i < woodCount; i++)
        {
            var c = cells[i];
            if (!chunks.TryGetCell(c.X, c.Y, c.Z, out var id, out var state))
                continue;

            // The parent has to be attached full-cube wood of the species in the world: never
            // hang a shell off a core or a shell that did not make it there
            if (!WoodState.TryParentOffset(c.State, out var pdx, out var pdy, out var pdz)
                || !chunks.TryGetCell(c.X + pdx, c.Y + pdy, c.Z + pdz, out var pid, out var pstate)
                || pid != species.WoodId || WoodState.IsDetached(pstate) || !WoodState.IsFullCube(pstate))
                continue;

            if (id == species.WoodId && WoodState.IsShell(state) && !WoodState.IsDetached(state))
            {
                if (state != c.State)
                {
                    Write(chunks, c.X, c.Y, c.Z, c.Block, c.State, log, idChanged: false, mesh: true);
                    woodWritten++;
                }
                continue;
            }

            // New ring only: inside the footprint an original core already had, a missing shell
            // was mined or cut and stays that way, like a mined leaf
            var core = graph.NodeAt(rx, c.Y, rz);
            int dx = c.X - rx, dz = c.Z - rz;
            var inOldRing = oldRadius != null && core >= 0 && core < originalCount
                            && TreeRasterizer.InFootprint(species.TrunkShape, oldRadius[core], dx, dz);
            if (!inOldRing && IsOwnGrowable(species, id, state) && space.IsFree(c.X, c.Y, c.Z))
            {
                Write(chunks, c.X, c.Y, c.Z, c.Block, c.State, log, idChanged: true, mesh: true);
                woodWritten++;
            }
        }

        // Leaves regrow only inside the clusters of tips this call changed, plus, when the trunk
        // thickened enough to widen the crown, every original tip still in the world: the radius
        // is one per tree, so a thicker root widens every cluster at once. A skipped original
        // node (its cell unloaded, or its parent never reached the world) is left out, because
        // Rasterize still yields it as a live tip and leaves around it would hang off wood that
        // is not there. New nodes are covered by changed[i], as before
        var regrow = new HashSet<long>();
        var radius = species.HasLeaves ? LeafCluster.ClusterRadius(species, graph) : 0;
        var crownWidened = originalCount > 0 && radius != radiusBefore;
        for (var i = 0; i < graph.Count && species.HasLeaves; i++)
        {
            if (!(changed[i] || (crownWidened && i < originalCount && !skipped[i]))
                || !LeafCluster.IsLiveTip(graph, species, i))
                continue;
            int tx = graph[i].X, ty = graph[i].Y, tz = graph[i].Z;
            for (var dx = -radius; dx <= radius; dx++)
            for (var dy = -radius; dy <= radius; dy++)
            for (var dz = -radius; dz <= radius; dz++)
                if (LeafCluster.HydrationFromTip(species, radius, dx, dy, dz) >= 1)
                    regrow.Add(TreeCellKey.Pack(tx + dx, ty + dy, tz + dz));
        }

        // Leaf pass: new leaves into air, hydration only ever raised on the species' own leaves
        var leavesPlaced = 0;
        var hydrationRaised = 0;
        for (var i = woodCount; i < cells.Count && regrow.Count > 0; i++)
        {
            var c = cells[i];
            if (!regrow.Contains(TreeCellKey.Pack(c.X, c.Y, c.Z)))
                continue;
            if (!chunks.TryGetCell(c.X, c.Y, c.Z, out var id, out var state))
                continue;

            if (id == 0)
            {
                if (!space.IsFree(c.X, c.Y, c.Z))
                    continue;
                Write(chunks, c.X, c.Y, c.Z, c.Block, c.State, log, idChanged: true, mesh: true);
                leavesPlaced++;
            }
            else if (id == species.LeavesId && !LeafState.IsPersistent(state))
            {
                var hydration = LeafState.Hydration(c.State);
                if (LeafState.Hydration(state) >= hydration)
                    continue;
                // The mesher never reads leaf state, so a raise remeshes nothing
                Write(chunks, c.X, c.Y, c.Z, id, LeafState.WithHydration(state, hydration), log,
                    idChanged: false, mesh: false);
                hydrationRaised++;
            }
        }

        FireHooks(chunks, log, fluids, leafDecay, lod);
        return new TreeGrowthResult(status, rx, ry, rz, added, woodWritten, leavesPlaced, hydrationRaised);
    }

    // Air or a non-persistent leaf of the species: the cells a new node may take
    private static bool IsOwnGrowable(TreeSpecies species, byte id, byte state) =>
        id == 0 || (species.HasLeaves && id == species.LeavesId && !LeafState.IsPersistent(state));

    /// <summary>One raw cell write plus its bookkeeping.</summary>
    private static void Write(ChunkManager chunks, int x, int y, int z, byte id, byte state, WriteLog log,
        bool idChanged, bool mesh)
    {
        chunks.ChunkAt(x, y, z)!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        log.Record(x, y, z, idChanged, mesh);
    }

    /// <summary>Runs every edit hook once for the call's writes.</summary>
    private static void FireHooks(ChunkManager chunks, WriteLog log, FluidManager? fluids,
        LeafDecayManager? leafDecay, LodManager? lod)
    {
        if (!log.Any)
            return;

        // Water beside a cell that changed id has to notice it
        if (fluids != null)
            foreach (var (x, y, z) in log.IdChanged)
                fluids.ScheduleAround(x, y, z);

        // Decay prunes the leaves the old tips fed, and is the only pruner
        leafDecay?.ScheduleBox(log.Min, log.Max);

        foreach (var (cx, cy, cz) in log.Chunks)
            chunks.MarkDirty(new Vector3(cx, cy, cz));

        foreach (var (x, z) in log.Columns)
        {
            TreeGenerator.PoisonCanopy(x, z, 0);
            lod?.MarkEditedColumn(x, z);
        }

        // No FallingBlockManager.TryTopple and no LodSurface edit (NotifyLodEdit): growth only
        // fills air, leaves or cover and never removes or writes a surface solid, so nothing
        // above can lose its support and no column's surface height changes.
    }

    /// <summary>
    /// The writes of one call: the box over every written cell, the chunks a mesh-affecting write
    /// touched (its own and the face-adjacent chunk across each border it sits on), the cells whose
    /// id changed and the columns with a mesh-affecting write.
    /// </summary>
    private sealed class WriteLog
    {
        public Vector3 Min = new(float.MaxValue);
        public Vector3 Max = new(float.MinValue);
        public bool Any;
        public readonly List<(int X, int Y, int Z)> IdChanged = [];
        public readonly HashSet<(int X, int Y, int Z)> Chunks = [];
        public readonly HashSet<(int X, int Z)> Columns = [];

        public void Record(int x, int y, int z, bool idChanged, bool mesh)
        {
            Any = true;
            var cell = new Vector3(x, y, z);
            Min = Vector3.Min(Min, cell);
            Max = Vector3.Max(Max, cell);
            if (idChanged)
                IdChanged.Add((x, y, z));
            if (!mesh)
                return;

            Columns.Add((x, z));
            int cx = x >> Chunk.SizeShift, cy = y >> Chunk.SizeShift, cz = z >> Chunk.SizeShift;
            Chunks.Add((cx, cy, cz));
            int lx = x & Chunk.SizeMask, ly = y & Chunk.SizeMask, lz = z & Chunk.SizeMask;
            if (lx == 0) Chunks.Add((cx - 1, cy, cz));
            else if (lx == Chunk.SizeMask) Chunks.Add((cx + 1, cy, cz));
            if (ly == 0) Chunks.Add((cx, cy - 1, cz));
            else if (ly == Chunk.SizeMask) Chunks.Add((cx, cy + 1, cz));
            if (lz == 0) Chunks.Add((cx, cy, cz - 1));
            else if (lz == Chunk.SizeMask) Chunks.Add((cx, cy, cz + 1));
        }
    }
}
