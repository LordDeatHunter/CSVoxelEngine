using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The one rule that decides which tree wood stands: <b>cells that lose every path to an anchor
/// fall</b>. Paths run over parent edges (<see cref="TreeWoodShape.Connects"/>) plus vertical
/// shell-to-shell adjacency, so a wide trunk's ring holds itself up slice by slice even where the
/// shells' parent chains toward the core are cut; anchors are root cells and shells standing on
/// real ground (<see cref="IsGround"/>: solid, not tree wood, not leaves). A wide trunk therefore
/// falls only once a whole slice is cut. Shared by felling (<see cref="TopplingTree.Collect"/>)
/// and the orphan crumble (<c>LeafDecayManager</c>), so the two can never disagree.
/// <para>
/// GL-free reusable scratch with one owner: main thread only, not thread-safe. A search is an
/// iterative depth-first walk that tries Down first, then the cell's own parent direction, then
/// the sides, then Up, so a standing trunk usually reaches its anchor in about its height in
/// reads; it is bounded by the cap passed to the constructor.
/// </para>
/// </summary>
public sealed class TreeSupport(int cap)
{
    private readonly HashSet<long> _visited = new();
    private readonly List<(int X, int Y, int Z, byte State)> _component = new();
    private readonly Stack<int> _stack = new();
    private readonly int[] _order = new int[TreeDirections.Count];

    /// <summary>Every cell the last search reached, start first; the start cell's whole
    /// component once the search returned <see cref="TreeSupportResult.Unsupported"/>.</summary>
    public IReadOnlyList<(int X, int Y, int Z, byte State)> Component => _component;

    /// <summary>Packed keys of every cell the last search reached.</summary>
    public IEnumerable<long> VisitedKeys => _visited;

    /// <summary>Whether cell A links to its neighbour B lying in direction
    /// <paramref name="dirAtoB"/>: a parent edge, or two attached shells of the same tree wood
    /// stacked vertically. Symmetric, like <see cref="TreeWoodShape.Connects"/>.</summary>
    public static bool Links(byte idA, byte stateA, byte idB, byte stateB, int dirAtoB)
    {
        if (TreeWoodShape.Connects(idA, stateA, idB, stateB, dirAtoB))
            return true;
        return TreeDirections.IsVertical(dirAtoB)
               && idA == idB && BlockType.IsTreeWood(idA)
               && !WoodState.IsDetached(stateA) && !WoodState.IsDetached(stateB)
               && WoodState.IsShell(stateA) && WoodState.IsShell(stateB);
    }

    /// <summary>Whether a block holds a shell up: solid, and neither tree wood nor leaves, so a
    /// wide trunk resting on another tree or on a canopy is not grounded.</summary>
    public static bool IsGround(byte id) =>
        BlockType.IsSolid(id) && !BlockType.IsTreeWood(id) && !TreeGenerator.IsLeaf(id);

    /// <summary>Whether a wood cell is an anchor: a root, or a shell whose cell below
    /// (<paramref name="belowId"/>) is real ground.</summary>
    public static bool IsAnchor(byte state, byte belowId) =>
        WoodState.IsRoot(state) || (WoodState.IsShell(state) && IsGround(belowId));

    /// <summary>
    /// Searches from the wood cell at (x, y, z) for an anchor. With <paramref name="hasExcluded"/>
    /// the cell (ex, ey, ez) reads as absent (the cut cell of a fell, which may still be in the
    /// world). An unloaded read returns <see cref="TreeSupportResult.Unknown"/> when
    /// <paramref name="unloadedIsUnknown"/> is set, and is otherwise no link. A cell whose key is
    /// in <paramref name="knownSupported"/> counts as anchored. Past the cap the result is
    /// <see cref="TreeSupportResult.Unknown"/>. A start cell that is not attached tree wood
    /// belongs to no tree and reports <see cref="TreeSupportResult.Supported"/>.
    /// </summary>
    public TreeSupportResult Search(TreeCellReader read, int x, int y, int z,
        bool hasExcluded, int ex, int ey, int ez, bool unloadedIsUnknown, HashSet<long>? knownSupported = null)
    {
        _visited.Clear();
        _component.Clear();
        _stack.Clear();

        if (hasExcluded && x == ex && y == ey && z == ez)
            return TreeSupportResult.Unsupported;
        if (!read(x, y, z, out var id, out var startState))
            return unloadedIsUnknown ? TreeSupportResult.Unknown : TreeSupportResult.Unsupported;
        if (!BlockType.IsTreeWood(id) || WoodState.IsDetached(startState))
            return TreeSupportResult.Supported;

        _visited.Add(TreeCellKey.Pack(x, y, z));
        _component.Add((x, y, z, startState));
        _stack.Push(0);

        while (_stack.Count > 0)
        {
            var (cx, cy, cz, state) = _component[_stack.Pop()];
            if (WoodState.IsRoot(state)
                || (knownSupported != null && knownSupported.Contains(TreeCellKey.Pack(cx, cy, cz))))
                return TreeSupportResult.Supported;

            // Down first: it decides a shell's anchor and is the shortest way home for a trunk
            var orderCount = 0;
            _order[orderCount++] = TreeDirections.Down;
            if (WoodState.HasParentCell(state) && WoodState.Parent(state) != TreeDirections.Down)
                _order[orderCount++] = WoodState.Parent(state);
            for (var dir = 0; dir < TreeDirections.Count; dir++)
                if (!TreeDirections.IsVertical(dir) && !(WoodState.HasParentCell(state) && WoodState.Parent(state) == dir))
                    _order[orderCount++] = dir;
            if (!(WoodState.HasParentCell(state) && WoodState.Parent(state) == TreeDirections.Up))
                _order[orderCount++] = TreeDirections.Up;

            // Read every neighbour before pushing, then push in reverse so the stack pops them
            // in _order: Down, parent, sides, Up
            var pushFrom = _component.Count;
            for (var i = 0; i < orderCount; i++)
            {
                var dir = _order[i];
                var nx = cx + TreeDirections.Dx[dir];
                var ny = cy + TreeDirections.Dy[dir];
                var nz = cz + TreeDirections.Dz[dir];

                byte nid, nstate;
                if (hasExcluded && nx == ex && ny == ey && nz == ez)
                {
                    nid = 0;
                    nstate = 0;
                }
                else if (!read(nx, ny, nz, out nid, out nstate))
                {
                    if (unloadedIsUnknown)
                        return TreeSupportResult.Unknown;
                    nid = 0;
                    nstate = 0;
                }

                if (dir == TreeDirections.Down && WoodState.IsShell(state) && IsGround(nid))
                    return TreeSupportResult.Supported;

                if (!Links(id, state, nid, nstate, dir) || !_visited.Add(TreeCellKey.Pack(nx, ny, nz)))
                    continue;
                if (_visited.Count > cap)
                    return TreeSupportResult.Unknown;
                _component.Add((nx, ny, nz, nstate));
            }

            for (var i = _component.Count - 1; i >= pushFrom; i--)
                _stack.Push(i);
        }

        return TreeSupportResult.Unsupported;
    }
}
