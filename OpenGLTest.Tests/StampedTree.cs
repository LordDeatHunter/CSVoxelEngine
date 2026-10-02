using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A grown species stamped into a headless <see cref="ChunkManager"/>, shared by the felling and
/// leaf-decay rules: the species is grown on a flat <see cref="VirtualTreeSpace"/>, rasterised and
/// written wood first, then leaves into air only, exactly like worldgen, so the facts read the
/// same wood and leaf state bytes worldgen writes.
/// </summary>
internal sealed class StampedTree
{
    public required TreeSpecies Species { get; init; }
    public required TreeGraph Graph { get; init; }
    public required ChunkManager Chunks { get; init; }
    public required Dictionary<(int X, int Y, int Z), (byte Id, byte State)> Wood { get; init; }
    public required Dictionary<(int X, int Y, int Z), (byte Id, byte State)> Leaves { get; init; }

    /// <summary>Every worldgen species, in ordinal name order.</summary>
    internal static IEnumerable<TreeSpecies> WorldgenSpecies =>
        TreeSpeciesRegistry.All.Where(s => s.Worldgen != null).OrderBy(s => s.Name, StringComparer.Ordinal);

    public Vector3 CellOf(int node)
    {
        var n = Graph[node];
        return new Vector3(n.X, n.Y, n.Z);
    }

    public (byte Id, byte State) Read((int X, int Y, int Z) cell)
    {
        Assert.True(Chunks.TryGetCell(cell.X, cell.Y, cell.Z, out var id, out var state));
        return (id, state);
    }

    /// <summary>Writes a cell, creating its chunk (block data ready, all air) on first use.</summary>
    public void Write((int X, int Y, int Z) cell, byte id, byte state) => WriteCell(Chunks, cell, id, state);

    /// <summary>Writes a cell, creating its chunk (block data ready, all air) on first use.</summary>
    internal static void WriteCell(ChunkManager chunks, (int X, int Y, int Z) cell, byte id, byte state)
    {
        var pos = new Vector3(cell.X >> Chunk.SizeShift, cell.Y >> Chunk.SizeShift, cell.Z >> Chunk.SizeShift);
        if (!chunks.Chunks.TryGetValue(pos, out var chunk))
        {
            chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            chunks.Chunks[pos] = chunk;
        }

        chunk.SetBlockRaw(cell.X & Chunk.SizeMask, cell.Y & Chunk.SizeMask, cell.Z & Chunk.SizeMask, id, state);
    }

    /// <summary>Grows the species on flat ground and stamps it like worldgen: wood first, then
    /// leaves into air only.</summary>
    internal static StampedTree Stamp(TreeSpecies species, int seed, int rootX, int rootY, int rootZ)
    {
        var space = new VirtualTreeSpace((_, _) => rootY - 1);
        var graph = TreeGrower.GrowToMaturity(species, rootX, rootY, rootZ, space, seed);
        var cells = TreeRasterizer.Rasterize(graph, species, space);

        var chunks = new ChunkManager(null!);
        var wood = new Dictionary<(int, int, int), (byte, byte)>();
        var leaves = new Dictionary<(int, int, int), (byte, byte)>();
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            var key = (c.X, c.Y, c.Z);
            if (i < graph.Count)
            {
                wood[key] = (c.Block, c.State);
                WriteCell(chunks, key, c.Block, c.State);
            }
            else if (!wood.ContainsKey(key) && !leaves.ContainsKey(key))
            {
                leaves[key] = (c.Block, c.State);
                WriteCell(chunks, key, c.Block, c.State);
            }
        }

        Assert.True(graph.Count > 1, $"{species.Name} grew a single cell; nothing to fell");
        return new StampedTree { Species = species, Graph = graph, Chunks = chunks, Wood = wood, Leaves = leaves };
    }

    /// <summary>Node indices of the subtree under <paramref name="node"/>, itself included.</summary>
    internal static HashSet<int> SubtreeOf(TreeGraph graph, int node)
    {
        var inside = new bool[graph.Count];
        var result = new HashSet<int>();
        for (var i = node; i < graph.Count; i++)
        {
            // Children always index above their parents, so one forward sweep sees every parent first
            if (i != node && (graph[i].Parent < 0 || !inside[graph[i].Parent]))
                continue;
            inside[i] = true;
            result.Add(i);
        }

        return result;
    }

    /// <summary>The smallest proper limb: a non-root, non-feature node with children whose subtree
    /// leaves the most of the tree standing.</summary>
    internal static int PickLimb(TreeGraph graph)
    {
        var best = -1;
        var bestSize = int.MaxValue;
        for (var i = 1; i < graph.Count; i++)
        {
            if (graph[i].IsFeature || graph.ChildCount(i) == 0)
                continue;
            var size = SubtreeOf(graph, i).Count;
            if (size >= bestSize)
                continue;
            best = i;
            bestSize = size;
        }

        return best;
    }
}
