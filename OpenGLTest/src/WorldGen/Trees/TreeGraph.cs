using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The virtual node graph one tree grows in before it is rasterised: one node per wood cell,
/// int-indexed, with the root (the base cell) at index <see cref="Root"/>. Every node but the
/// root has a parent one face step away, and children always index above their parents, so a
/// reverse index sweep is a valid post-order. Per-tree mutable state with a single owner; not
/// thread-safe.
/// </summary>
public sealed class TreeGraph
{
    /// <summary>One wood cell of the tree.</summary>
    public struct Node
    {
        /// <summary>World cell of the node.</summary>
        public int X, Y, Z;

        /// <summary>Index of the parent node, -1 on the root.</summary>
        public int Parent;

        /// <summary>Direction 0..5 toward the parent, <see cref="WoodState.ParentRoot"/> on the root.</summary>
        public int ParentDir;

        /// <summary>First child and next sibling indices, -1 for none.</summary>
        public int FirstChild, NextSibling;

        /// <summary>Number of children.</summary>
        public int ChildCount;

        /// <summary>Raw pipe-model diameter in pixels, girth-free.</summary>
        public float PipePx;

        /// <summary>Displayed diameter in pixels, what the rasterizer encodes.</summary>
        public float ThicknessPx;

        /// <summary>Whether the node was appended by a post-growth feature (roots).</summary>
        public bool IsFeature;

        /// <summary>
        /// Lower bound on the displayed thickness. A node rebuilt from the world gets its stored
        /// diameter, so a rebuilt tree never thins (a cut limb does not shrink the trunk); 0 for
        /// grown nodes.
        /// </summary>
        public float FloorPx;
    }

    /// <summary>Index of the root node.</summary>
    public const int Root = 0;

    private Node[] _nodes = new Node[32];
    private int _count;
    // Membership only (cell -> node index); never iterated, so its order never leaks into output.
    private readonly Dictionary<long, int> _cells = new();
    private int _maxY;

    /// <summary>Creates a graph holding only the root at the given cell.</summary>
    public TreeGraph(int x, int y, int z, float thicknessPx)
    {
        _nodes[Root] = new Node
        {
            X = x, Y = y, Z = z,
            Parent = -1,
            ParentDir = WoodState.ParentRoot,
            FirstChild = -1,
            NextSibling = -1,
            PipePx = thicknessPx,
            ThicknessPx = thicknessPx,
        };
        _count = 1;
        _cells[TreeCellKey.Pack(x, y, z)] = Root;
        _maxY = y;
    }

    /// <summary>
    /// Rebuilds a tree's graph from the world's wood state bytes, starting at the root cell
    /// (x, y, z), which must hold <see cref="TreeSpecies.WoodId"/> with the
    /// <see cref="WoodState.ParentRoot"/> parent code; null otherwise. The walk is breadth-first
    /// over child edges only: a face neighbour is a child when it holds the species' wood, is
    /// not detached and its stored parent offset lands exactly on the current cell. Detached
    /// cells and cells whose parent no longer exists are therefore never reached, so a cut branch
    /// stays cut. A node below the root's level loads as a feature node (roots), which is exact
    /// because <see cref="TreeGrower.Pulse"/> never descends. Every node's thickness floor
    /// (<see cref="Node.FloorPx"/>) is its stored diameter, so a later
    /// <see cref="TreeGrower.Thicken"/> never thins the tree; until the caller runs it, the
    /// thickness floats are just those integer diameters. Returns null when a read hits an
    /// unloaded chunk (the tree may continue there, and a partial graph must never grow) or the
    /// walk passes <see cref="WorldGenerationConfig.TreeGrowNodeCap"/> nodes.
    /// </summary>
    public static TreeGraph? FromWorld(TreeCellReader read, TreeSpecies species, int x, int y, int z)
    {
        if (!read(x, y, z, out var rootId, out var rootState)
            || rootId != species.WoodId
            || !WoodState.IsRoot(rootState))
            return null;

        float rootPx = WoodState.DiameterPx(rootState);
        var graph = new TreeGraph(x, y, z, rootPx);
        graph.SetFloor(Root, rootPx);

        // The node index is the queue, so children always index above their parents.
        for (var head = 0; head < graph.Count; head++)
        {
            // Copy before any AddChild: the node reference is invalidated by a reallocation.
            int cx = graph._nodes[head].X, cy = graph._nodes[head].Y, cz = graph._nodes[head].Z;
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                int nx = cx + TreeDirections.Dx[dir];
                int ny = cy + TreeDirections.Dy[dir];
                int nz = cz + TreeDirections.Dz[dir];
                if (!read(nx, ny, nz, out var id, out var state))
                    return null;
                if (id != species.WoodId || WoodState.IsDetached(state))
                    continue;
                if (!WoodState.TryParentOffset(state, out var pdx, out var pdy, out var pdz)
                    || nx + pdx != cx || ny + pdy != cy || nz + pdz != cz)
                    continue;
                if (graph.Occupies(nx, ny, nz))
                    continue;
                if (graph.Count >= WorldGenerationConfig.TreeGrowNodeCap)
                    return null;

                float d = WoodState.DiameterPx(state);
                var child = graph.AddChild(head, dir, d, feature: ny < y);
                graph.SetFloor(child, d);
            }
        }

        return graph;
    }

    /// <summary>Number of nodes, root included.</summary>
    public int Count => _count;

    /// <summary>Root cell X.</summary>
    public int RootX => _nodes[Root].X;

    /// <summary>Root cell Y.</summary>
    public int RootY => _nodes[Root].Y;

    /// <summary>Root cell Z.</summary>
    public int RootZ => _nodes[Root].Z;

    /// <summary>Highest node Y in the graph.</summary>
    public int MaxY => _maxY;

    /// <summary>
    /// The node at index <paramref name="i"/>. The reference points into the node array, which
    /// <see cref="AddChild"/> may reallocate: copy the fields you need before adding a node and
    /// never hold the reference across an add.
    /// </summary>
    public ref readonly Node this[int i]
    {
        get
        {
            CheckIndex(i);
            return ref _nodes[i];
        }
    }

    /// <summary>Number of children of node <paramref name="i"/>.</summary>
    public int ChildCount(int i)
    {
        CheckIndex(i);
        return _nodes[i].ChildCount;
    }

    /// <summary>
    /// Appends a child one face step from <paramref name="parent"/> in direction
    /// <paramref name="dir"/> and returns its index, which is always above the parent's.
    /// Throws when the cell is already held by the graph.
    /// </summary>
    public int AddChild(int parent, int dir, float thicknessPx, bool feature = false)
    {
        CheckIndex(parent);
        CheckDir(dir);

        var x = _nodes[parent].X + TreeDirections.Dx[dir];
        var y = _nodes[parent].Y + TreeDirections.Dy[dir];
        var z = _nodes[parent].Z + TreeDirections.Dz[dir];
        var key = TreeCellKey.Pack(x, y, z);
        if (_cells.ContainsKey(key))
            throw new InvalidOperationException($"The tree already holds the cell ({x}, {y}, {z}).");

        if (_count == _nodes.Length)
            Array.Resize(ref _nodes, _nodes.Length * 2);

        var index = _count++;
        _nodes[index] = new Node
        {
            X = x, Y = y, Z = z,
            Parent = parent,
            ParentDir = TreeDirections.Opposite(dir),
            FirstChild = -1,
            NextSibling = -1,
            PipePx = thicknessPx,
            ThicknessPx = thicknessPx,
            IsFeature = feature,
        };
        _cells[key] = index;

        // Append at the tail so the child list keeps insertion order.
        if (_nodes[parent].FirstChild < 0)
        {
            _nodes[parent].FirstChild = index;
        }
        else
        {
            var c = _nodes[parent].FirstChild;
            while (_nodes[c].NextSibling >= 0)
                c = _nodes[c].NextSibling;
            _nodes[c].NextSibling = index;
        }
        _nodes[parent].ChildCount++;

        if (y > _maxY)
            _maxY = y;
        return index;
    }

    /// <summary>The child of <paramref name="node"/> one step in <paramref name="dir"/>, or -1.</summary>
    public int ChildIn(int node, int dir)
    {
        CheckIndex(node);
        CheckDir(dir);
        var x = _nodes[node].X + TreeDirections.Dx[dir];
        var y = _nodes[node].Y + TreeDirections.Dy[dir];
        var z = _nodes[node].Z + TreeDirections.Dz[dir];
        for (var c = _nodes[node].FirstChild; c >= 0; c = _nodes[c].NextSibling)
        {
            if (_nodes[c].X == x && _nodes[c].Y == y && _nodes[c].Z == z)
                return c;
        }
        return -1;
    }

    /// <summary>Whether the graph holds a node at the cell.</summary>
    public bool Occupies(int x, int y, int z) => _cells.ContainsKey(TreeCellKey.Pack(x, y, z));

    /// <summary>Index of the node at the cell, or -1 when the graph holds none there.</summary>
    public int NodeAt(int x, int y, int z) =>
        _cells.TryGetValue(TreeCellKey.Pack(x, y, z), out var i) ? i : -1;

    /// <summary>Sets a node's raw pipe and displayed diameters.</summary>
    public void SetThickness(int i, float pipePx, float thicknessPx)
    {
        CheckIndex(i);
        _nodes[i].PipePx = pipePx;
        _nodes[i].ThicknessPx = thicknessPx;
    }

    /// <summary>Sets a node's thickness floor (<see cref="Node.FloorPx"/>).</summary>
    public void SetFloor(int i, float floorPx)
    {
        CheckIndex(i);
        _nodes[i].FloorPx = floorPx;
    }

    /// <summary>Whether two nodes sit exactly one face step apart.</summary>
    public bool IsFaceAdjacent(int a, int b)
    {
        CheckIndex(a);
        CheckIndex(b);
        return Math.Abs(_nodes[a].X - _nodes[b].X)
             + Math.Abs(_nodes[a].Y - _nodes[b].Y)
             + Math.Abs(_nodes[a].Z - _nodes[b].Z) == 1;
    }

    private void CheckIndex(int i)
    {
        if ((uint)i >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(i), i, $"Node index must be 0..{_count - 1}.");
    }

    private static void CheckDir(int dir)
    {
        if ((uint)dir >= TreeDirections.Count)
            throw new ArgumentOutOfRangeException(nameof(dir), dir, $"Direction must be 0..{TreeDirections.Count - 1}.");
    }
}
