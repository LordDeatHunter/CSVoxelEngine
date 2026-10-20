using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation.LeafDecay;

/// <summary>The orphaned-wood support test: a wood cell's parent-chain walk, the full <see cref="TreeSupport"/> search on a broken chain, and their frame memos. Owned by <see cref="LeafDecayManager"/>; main thread only.</summary>
internal sealed class OrphanWoodCheck
{
    private readonly ChunkCellCache _cells;

    // The orphan rule: TreeSupport's search over the decay's cached reads, shared with felling
    private readonly TreeCellReader _read;
    private readonly TreeSupport _support = new(SimulationConfig.TreeFellWoodCap);

    // Wood cells (CellKey) known anchored or known unanchored this frame; cleared at the top
    // of every Update, since an edit outside the decay can change either between frames
    private readonly HashSet<long> _supported = [];
    private readonly HashSet<long> _unsupported = [];

    // The parent-chain fast path's walked cells, reused
    private readonly List<long> _chain = [];

    internal OrphanWoodCheck(ChunkCellCache cells)
    {
        _cells = cells;
        _read = cells.TryGetCell;
    }

    /// <summary>Forgets the frame's support memos; called at the top of every Update.</summary>
    internal void BeginFrame()
    {
        _supported.Clear();
        _unsupported.Clear();
    }

    /// <summary>Whether a wood cell with a parent cell has lost every path to an anchor: its parent chain first, the full search only when the chain broke, both answers memoized for the frame. False as well when a read hit an unloaded chunk, which freezes the cell.</summary>
    internal bool IsOrphaned(int x, int y, int z, byte id, byte state)
    {
        if (!_unsupported.Contains(CellKey.Pack(x, y, z)))
        {
            var chain = WalkParentChain(x, y, z, id, state);
            if (chain != TreeSupportResult.Unsupported) return false;

            // The chain broke: only the full search can tell a held cell from an orphan
            var result = _support.Search(_read, x, y, z, hasExcluded: false, 0, 0, 0,
                unloadedIsUnknown: true, knownSupported: _supported);
            if (result == TreeSupportResult.Unknown) return false;
            if (result == TreeSupportResult.Supported)
            {
                // Every visited cell is linked to this one, so it reaches the same anchor
                foreach (var visited in _support.VisitedKeys) _supported.Add(visited);
                return false;
            }

            foreach (var c in _support.Component) _unsupported.Add(CellKey.Pack(c.X, c.Y, c.Z));
        }

        return true;
    }

    /// <summary>
    /// The fast half of the orphan test: follows the cell's parent chain while every step links
    /// (<see cref="TreeSupport.Links"/>). <see cref="TreeSupportResult.Supported"/> when it reaches
    /// an anchor or a cell already known anchored this frame (every walked cell is then memoized
    /// as anchored, since it reaches the same anchor), <see cref="TreeSupportResult.Unknown"/>
    /// when a read hit an unloaded chunk, and <see cref="TreeSupportResult.Unsupported"/> when the
    /// chain broke or ran past the cap, which only means the full search has to decide.
    /// </summary>
    private TreeSupportResult WalkParentChain(int x, int y, int z, byte id, byte state)
    {
        _chain.Clear();
        for (var step = 0; step < SimulationConfig.TreeFellWoodCap; step++)
        {
            var cellKey = CellKey.Pack(x, y, z);
            _chain.Add(cellKey);

            var anchored = WoodState.IsRoot(state) || _supported.Contains(cellKey);
            if (!anchored && WoodState.IsShell(state))
            {
                if (!_cells.TryGetCell(x, y - 1, z, out var belowId, out _)) return TreeSupportResult.Unknown;
                anchored = TreeSupport.IsAnchor(state, belowId);
            }

            if (anchored)
            {
                foreach (var walked in _chain) _supported.Add(walked);
                return TreeSupportResult.Supported;
            }

            if (!WoodState.HasParentCell(state)) return TreeSupportResult.Unsupported;

            var dir = WoodState.Parent(state);
            var px = x + TreeDirections.Dx[dir];
            var py = y + TreeDirections.Dy[dir];
            var pz = z + TreeDirections.Dz[dir];
            if (!_cells.TryGetCell(px, py, pz, out var pid, out var pstate)) return TreeSupportResult.Unknown;
            if (!TreeSupport.Links(id, state, pid, pstate, dir)) return TreeSupportResult.Unsupported;

            (x, y, z, id, state) = (px, py, pz, pid, pstate);
        }

        return TreeSupportResult.Unsupported;
    }
}
