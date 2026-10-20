using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Simulation.LeafDecay;

/// <summary>The leaf hydration rule on world cells: per leaf block the best-first offsets a tip hydrates, the live-tip test and the support a tip's tree gives its cluster, memoized for the frame. Owned by <see cref="LeafDecayManager"/>; main thread only.</summary>
internal sealed class TipHydration
{
    private readonly ChunkCellCache _cells;

    // Per leaf id, every offset from a tip to a cell that tip hydrates (h >= 1), sorted by
    // hydration descending: the first live tip a scan meets gives the cell its best hydration,
    // so an intact leaf usually stops after a candidate or two
    private readonly TipOffset[]?[] _offsetsByLeaf = new TipOffset[]?[256];

    private readonly record struct TipOffset(int Dx, int Dy, int Dz, int Hydration);

    // Wood cells (CellKey) whose parent chain's support is known this frame: the root
    // diameter a live tip's cluster radius scales with. Cleared at the top of every Update, like
    // the support memos; every cell of one walked chain shares its end and therefore its answer
    private readonly Dictionary<long, int> _tipSupport = [];

    // The support walk's visited cells, reused
    private readonly List<long> _supportChain = [];

    internal TipHydration(ChunkCellCache cells) => _cells = cells;

    /// <summary>Builds a leafy species' best-first tip offsets; called once per leafy species while the manager is constructed.</summary>
    internal void AddSpecies(TreeSpecies species) => _offsetsByLeaf[species.LeavesId] = TipOffsets(species);

    /// <summary>Forgets the frame's tip-support memo; called at the top of every Update.</summary>
    internal void BeginFrame() => _tipSupport.Clear();

    // Every offset a tip of the species hydrates, best first (a leaf is never its own tip);
    // OrderByDescending is stable, so ties keep the fixed x, y, z scan order. Built at the full
    // LeafRadius, the largest cluster any tree of the species wears: a smaller tree's cluster is
    // a subset of it (shapes are nested) with the same h on every shared offset, so
    // TryHydrationAt only has to drop the offsets outside the tip's own radius
    // and the best-first order stays exact
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

    /// <summary>
    /// The exact hydration rule on world cells: the best per-tip hydration over every live tip
    /// of the species whose cluster holds (x, y, z), capped at <see cref="LeafState.MaxHydration"/>.
    /// False when a read hit an unloaded chunk.
    /// </summary>
    internal bool TryHydrationAt(TreeSpecies species, int x, int y, int z, out int hydration)
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
            if (!_cells.TryGetCell(tx, ty, tz, out var tid, out var tstate)) return false;
            if (tid != species.WoodId) continue;
            if (!IsLiveTip(species, tx, ty, tz, tstate, out var unloaded))
            {
                if (unloaded) return false;
                continue;
            }

            // The table is the full-radius cluster; this tree's cluster may be smaller. The
            // best-first exit stays exact: a smaller radius only drops offsets (shapes are
            // nested) and never changes h, so the first tip whose own cluster holds the cell is
            // still the best one
            if (!TryTipSupport(species, tx, ty, tz, tstate, out var support)) return false;
            if (!LeafCluster.InShape(species.LeafShape, LeafCluster.ClusterRadius(species, support), dx, dy, dz))
                continue;

            hydration = Math.Min(h, LeafState.MaxHydration);
            return true;
        }

        return true;
    }

    /// <summary>
    /// Whether a cell of the species' wood is a live tip: not detached, no thicker than the
    /// species' tip limit, and no face neighbor of the same wood naming it as its parent.
    /// <paramref name="unloaded"/> reports a neighbor read that hit an unloaded chunk.
    /// </summary>
    private bool IsLiveTip(TreeSpecies species, int tx, int ty, int tz, byte tstate, out bool unloaded)
    {
        unloaded = false;
        // A shell rings a wide trunk core and is never a branch end
        if (WoodState.IsDetached(tstate) || WoodState.IsShell(tstate)
            || WoodState.DiameterPx(tstate) > species.TipMaxPx) return false;

        for (var dir = 0; dir < TreeDirections.Count; dir++)
        {
            var nx = tx + TreeDirections.Dx[dir];
            var ny = ty + TreeDirections.Dy[dir];
            var nz = tz + TreeDirections.Dz[dir];
            if (!_cells.TryGetCell(nx, ny, nz, out var nid, out var nstate))
            {
                unloaded = true;
                return false;
            }

            // A child: the neighbor's parent points back at the candidate tip
            if (nid == species.WoodId && !WoodState.IsDetached(nstate)
                && WoodState.TryParentOffset(nstate, out var px, out var py, out var pz)
                && nx + px == tx && ny + py == ty && nz + pz == tz)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The support a live tip's tree gives its cluster: the <see cref="WoodState.DiameterPx"/> of
    /// the root cell its parent chain reaches, following the links <see cref="OrphanWoodCheck.WalkParentChain"/>
    /// follows (<see cref="TreeSupport.Links"/>) for at most
    /// <see cref="SimulationConfig.TreeFellWoodCap"/> steps. A chain that stops first (no
    /// parent cell, a broken link, the cap) answers with the last cell it reached, which only an
    /// orphaned branch does. Memoized for the frame on every walked cell, since they all end at
    /// the same cell. False when a read hit an unloaded chunk, which freezes the leaf.
    /// </summary>
    private bool TryTipSupport(TreeSpecies species, int tx, int ty, int tz, byte tstate, out int supportPx)
    {
        _supportChain.Clear();
        int x = tx, y = ty, z = tz;
        var id = species.WoodId;
        var state = tstate;
        supportPx = WoodState.DiameterPx(state);

        for (var step = 0; step < SimulationConfig.TreeFellWoodCap; step++)
        {
            var cellKey = CellKey.Pack(x, y, z);
            if (_tipSupport.TryGetValue(cellKey, out var known))
            {
                supportPx = known;
                break;
            }

            _supportChain.Add(cellKey);
            supportPx = WoodState.DiameterPx(state);
            if (WoodState.IsRoot(state) || !WoodState.HasParentCell(state)) break;

            var dir = WoodState.Parent(state);
            var px = x + TreeDirections.Dx[dir];
            var py = y + TreeDirections.Dy[dir];
            var pz = z + TreeDirections.Dz[dir];
            if (!_cells.TryGetCell(px, py, pz, out var pid, out var pstate)) return false;
            if (!TreeSupport.Links(id, state, pid, pstate, dir)) break;

            (x, y, z, id, state) = (px, py, pz, pid, pstate);
        }

        foreach (var walked in _supportChain) _tipSupport[walked] = supportPx;
        return true;
    }
}
