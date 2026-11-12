using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation;

/// <summary>
/// GL-free branch structure of a felled tree: the collected wood walked outward from the cut over
/// <see cref="TreeSupport.Links"/>, the main stem (at each fork, the child above, else the bigger subtree),
/// and every side branch hanging off it, so a branch that hits the ground can snap off whole. Leaves
/// belong to the nearest wood block and go with its branch.
/// </summary>
internal sealed class FellBranches
{
    private readonly List<int>[] _children;
    private readonly int[] _branchRoot;
    private readonly int[] _woodOf;

    public FellBranches(IReadOnlyList<TopplingTree.FellBlock> blocks, Vector3 pivot)
    {
        var count = blocks.Count;
        _children = new List<int>[count];
        _branchRoot = new int[count];
        _woodOf = new int[count];
        Array.Fill(_branchRoot, -1);
        Array.Fill(_woodOf, -1);

        var wood = new List<int>();
        var byCell = new Dictionary<long, int>();
        for (var i = 0; i < count; i++)
        {
            _children[i] = [];
            if (!BlockType.IsTreeWood(blocks[i].Id)) continue;
            wood.Add(i);
            byCell[Key(blocks[i].Cell)] = i;
            _woodOf[i] = i;
        }
        if (wood.Count == 0) return;

        // The walk starts at the wood nearest the cut and follows links outward
        var root = wood[0];
        var best = float.MaxValue;
        foreach (var i in wood)
        {
            var d = Vector3.DistanceSquared(blocks[i].Cell + new Vector3(0.5f), pivot);
            if (d >= best) continue;
            best = d;
            root = i;
        }

        var parent = new int[count];
        Array.Fill(parent, -2);
        parent[root] = -1;
        var order = new List<int> { root };
        for (var head = 0; head < order.Count; head++)
        {
            var at = order[head];
            var cell = blocks[at].Cell;
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                var next = cell + new Vector3(TreeDirections.Dx[dir], TreeDirections.Dy[dir], TreeDirections.Dz[dir]);
                if (!byCell.TryGetValue(Key(next), out var n) || parent[n] != -2) continue;
                if (!TreeSupport.Links(blocks[at].Id, blocks[at].State, blocks[n].Id, blocks[n].State, dir)) continue;
                parent[n] = at;
                _children[at].Add(n);
                order.Add(n);
            }
        }

        var size = new int[count];
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var at = order[k];
            size[at]++;
            if (parent[at] >= 0) size[parent[at]] += size[at];
        }

        var stem = new bool[count];
        stem[root] = true;
        foreach (var at in order)
        {
            if (!stem[at] || _children[at].Count == 0) continue;
            // The stem carries on upward when it can, and otherwise follows the bigger subtree
            var heavy = _children[at][0];
            var above = blocks[at].Cell + new Vector3(0f, 1f, 0f);
            foreach (var c in _children[at])
            {
                if (blocks[c].Cell == above) { heavy = c; break; }
                if (size[c] > size[heavy]) heavy = c;
            }
            stem[heavy] = true;
        }

        foreach (var at in order)
        {
            if (stem[at] || parent[at] < 0) continue;
            _branchRoot[at] = stem[parent[at]] ? at : _branchRoot[parent[at]];
        }

        // Wood the walk never reached is not part of the structure: nothing to snap off
        foreach (var i in wood)
        {
            if (parent[i] == -2) _woodOf[i] = -1;
        }

        for (var i = 0; i < count; i++)
        {
            if (!TreeGenerator.IsLeaf(blocks[i].Id)) continue;
            var nearest = -1;
            var nearestDistance = float.MaxValue;
            foreach (var w in wood)
            {
                if (parent[w] == -2) continue;
                var d = Vector3.DistanceSquared(blocks[i].Cell, blocks[w].Cell);
                if (d >= nearestDistance) continue;
                nearestDistance = d;
                nearest = w;
            }
            _woodOf[i] = nearest;
        }
    }

    /// <summary>The root of the side branch block <paramref name="index"/> hangs on (its own
    /// attachment to the main stem), or -1 when it is on the stem or belongs to no branch.</summary>
    public int BranchRootOf(int index)
    {
        var wood = _woodOf[index];
        return wood < 0 ? -1 : _branchRoot[wood];
    }

    /// <summary>Appends every block of the branch rooted at <paramref name="root"/>: its wood
    /// subtree and the leaves that hang on that wood.</summary>
    public void Members(int root, List<int> into)
    {
        var inBranch = new bool[_woodOf.Length];
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            inBranch[at] = true;
            foreach (var c in _children[at])
                stack.Push(c);
        }
        for (var i = 0; i < _woodOf.Length; i++)
        {
            if (_woodOf[i] >= 0 && inBranch[_woodOf[i]])
                into.Add(i);
        }
    }

    private static long Key(Vector3 cell) => CellKey.Pack((int)cell.X, (int)cell.Y, (int)cell.Z);
}
