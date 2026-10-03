using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A grown species stamped into a headless <see cref="ChunkManager"/>, shared by the felling and
/// leaf-decay rules: the species is grown on a flat <see cref="VirtualTreeSpace"/>, rasterised and
/// written wood first (nodes and any shells), then leaves into air only, exactly like worldgen, so
/// the facts read the same wood and leaf state bytes worldgen writes.
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

    /// <summary>The inclusive bounding box of the tree's wood and leaf cells.</summary>
    public (int X, int Y, int Z) Min => IntBounds(Wood.Keys.Concat(Leaves.Keys)).Min;

    /// <summary>The inclusive bounding box of the tree's wood and leaf cells.</summary>
    public (int X, int Y, int Z) Max => IntBounds(Wood.Keys.Concat(Leaves.Keys)).Max;

    /// <summary>Loads empty chunks over the tree's wood and leaf cells grown by
    /// <paramref name="margin"/>, so no read near the tree hits an unloaded chunk.</summary>
    public void LoadAround(int margin) => LoadAround(Chunks, Wood.Keys.Concat(Leaves.Keys), margin);

    /// <summary>The bounding box of <paramref name="cells"/> as float corners.</summary>
    internal static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<(int X, int Y, int Z)> cells)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (x, y, z) in cells)
        {
            min = Vector3.Min(min, new Vector3(x, y, z));
            max = Vector3.Max(max, new Vector3(x, y, z));
        }

        return (min, max);
    }

    private static ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) IntBounds(IEnumerable<(int X, int Y, int Z)> cells)
    {
        var (min, max) = Bounds(cells);
        return (((int)min.X, (int)min.Y, (int)min.Z), ((int)max.X, (int)max.Y, (int)max.Z));
    }

    /// <summary>Creates the chunk at chunk position (cx, cy, cz), all air with block data ready,
    /// unless it is already loaded.</summary>
    internal static void EnsureChunk(ChunkManager chunks, int cx, int cy, int cz)
    {
        var pos = new Vector3(cx, cy, cz);
        if (!chunks.Chunks.TryGetValue(pos, out _))
            chunks.Chunks[pos] = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
    }

    /// <summary>Creates every missing chunk (all air, block data ready) covering the bounding box
    /// of <paramref name="cells"/> grown by <paramref name="margin"/> on every side.</summary>
    internal static void LoadAround(ChunkManager chunks, IEnumerable<(int X, int Y, int Z)> cells, int margin)
    {
        var (min, max) = IntBounds(cells);
        for (var cx = (min.X - margin) >> Chunk.SizeShift; cx <= (max.X + margin) >> Chunk.SizeShift; cx++)
        for (var cy = (min.Y - margin) >> Chunk.SizeShift; cy <= (max.Y + margin) >> Chunk.SizeShift; cy++)
        for (var cz = (min.Z - margin) >> Chunk.SizeShift; cz <= (max.Z + margin) >> Chunk.SizeShift; cz++)
            EnsureChunk(chunks, cx, cy, cz);
    }

    /// <summary>Writes <paramref name="id"/> and <paramref name="state"/> into every cell of the
    /// inclusive box, creating chunks as needed.</summary>
    internal static void Fill(ChunkManager chunks, (int X, int Y, int Z) min, (int X, int Y, int Z) max, byte id, byte state = 0)
    {
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var z = min.Z; z <= max.Z; z++)
            WriteCell(chunks, (x, y, z), id, state);
    }

    /// <summary>Every loaded cell of the inclusive box with its id and state; cells in unloaded
    /// chunks are left out.</summary>
    internal static Dictionary<(int X, int Y, int Z), (byte Id, byte State)> Snapshot(
        ChunkManager chunks, (int X, int Y, int Z) min, (int X, int Y, int Z) max)
    {
        var result = new Dictionary<(int X, int Y, int Z), (byte Id, byte State)>();
        for (var x = min.X; x <= max.X; x++)
        for (var y = min.Y; y <= max.Y; y++)
        for (var z = min.Z; z <= max.Z; z++)
            if (chunks.TryGetCell(x, y, z, out var id, out var state))
                result[(x, y, z)] = (id, state);

        return result;
    }

    /// <summary>The wood cells that are graph nodes: every wood cell but a wide trunk's shells.</summary>
    public IEnumerable<(int X, int Y, int Z)> NodeWood =>
        Wood.Where(kv => !WoodState.IsShell(kv.Value.State)).Select(kv => kv.Key);

    /// <summary>Grows the species on flat ground and stamps it like worldgen: wood (nodes, then a
    /// wide trunk's shells) first, then leaves into air only. With <paramref name="steps"/> the
    /// tree is grown for exactly that many pulses, without features, instead of to maturity.</summary>
    internal static StampedTree Stamp(TreeSpecies species, int seed, int rootX, int rootY, int rootZ, int? steps = null)
    {
        var space = new VirtualTreeSpace((_, _) => rootY - 1);
        var graph = steps is { } n
            ? TreeGrower.GrowSteps(species, rootX, rootY, rootZ, space, seed, n, applyFeatures: false)
            : TreeGrower.GrowToMaturity(species, rootX, rootY, rootZ, space, seed);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out _);

        var chunks = new ChunkManager(null!);
        var wood = new Dictionary<(int, int, int), (byte, byte)>();
        var leaves = new Dictionary<(int, int, int), (byte, byte)>();
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            var key = (c.X, c.Y, c.Z);
            // Wood is nodes then a wide trunk's shells; everything else is a leaf
            if (c.Block == species.WoodId)
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

    /// <summary>
    /// The young step of a species' crown scaling: the smallest pulse count whose flat-ground,
    /// feature-free graph has a live tip other than the root while its
    /// <see cref="LeafCluster.ClusterRadius(TreeSpecies, TreeGraph)"/> is still below the full
    /// <see cref="TreeSpecies.LeafRadius"/>. Null when no step up to maturity has one.
    /// </summary>
    internal static int? YoungStep(TreeSpecies species, int seed, int rootX, int rootY, int rootZ)
    {
        var space = new VirtualTreeSpace((_, _) => rootY - 1);
        var maturity = TreeGrower.MaturityStepsAt(species, rootX, rootY, rootZ, seed);
        for (var steps = 1; steps <= maturity; steps++)
        {
            var graph = TreeGrower.GrowSteps(species, rootX, rootY, rootZ, space, seed, steps, applyFeatures: false);
            if (LeafCluster.ClusterRadius(species, graph) >= species.LeafRadius)
                continue;
            for (var i = 1; i < graph.Count; i++)
                if (LeafCluster.IsLiveTip(graph, species, i))
                    return steps;
        }

        return null;
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
