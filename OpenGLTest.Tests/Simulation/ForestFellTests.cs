using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Trees;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// A tree felled at its stump in a dense forest falls down and never up: every worldgen species is
/// stamped on stone ground with copies of itself crowding it on all sides, its root is cut the way
/// the game cuts it (the root stays as the stump, the fall is pinned on it, the world is felt through
/// <see cref="TopplingTree.FallStopper"/>), and no piece of it may rise as it falls.
/// </summary>
public sealed class ForestFellTests(ITestOutputHelper output)
{
    private const int Seed = 4242;
    private const int RootX = 40, RootY = 20, RootZ = 40;
    // How far a falling piece's center may rise above where it started (a pinned trunk tipping over
    // its stump lifts its center a hair before it drops)
    private const float RiseTolerance = 0.3f;

    private static readonly (int X, int Z)[] Neighbors =
        [(-6, 0), (6, 0), (0, -6), (0, 6), (-5, -5), (5, -5), (-5, 5), (5, 5), (4, 1), (-1, 4)];

    private static (StampedTree Tree, ChunkManager Chunks) Forest(TreeSpecies species)
    {
        var tree = StampedTree.Stamp(species, Seed, RootX, RootY, RootZ);
        var chunks = tree.Chunks;
        foreach (var (dx, dz) in Neighbors)
        {
            // Wood over air or leaves, never over the felled tree's wood; leaves into air only
            foreach (var ((x, y, z), (id, state)) in tree.Wood)
            {
                var cell = (x + dx, y, z + dz);
                if (!tree.Wood.ContainsKey(cell))
                    StampedTree.WriteCell(chunks, cell, id, state);
            }
            foreach (var ((x, y, z), (id, state)) in tree.Leaves)
            {
                var cell = (x + dx, y, z + dz);
                if (chunks.TryGetCell(cell.Item1, cell.Item2, cell.Item3, out var at, out _) && at == 0)
                    StampedTree.WriteCell(chunks, cell, id, state);
            }
        }

        tree.LoadAround(16);
        var (min, max) = (tree.Min, tree.Max);
        StampedTree.Fill(chunks, (min.X - 16, RootY - 4, min.Z - 16), (max.X + 16, RootY - 1, max.Z + 16),
            ContentRegistry.RequireBlock("stone").Id);
        return (tree, chunks);
    }

    /// <summary>Fells the tree at its root toward <paramref name="fall"/> and returns the most any
    /// piece's center rose above where that piece started, how far the tree's blocks ended below
    /// where they stood on average, and how many pieces the tree ended in.</summary>
    private static (float Rise, float Drop, int Pieces) Fell(TreeSpecies species, Vector3 fall)
    {
        var (tree, chunks) = Forest(species);
        var root = tree.CellOf(TreeGraph.Root);
        var (rootId, rootState) = tree.Read(((int)root.X, (int)root.Y, (int)root.Z));
        var collected = TopplingTree.Collect(chunks, root, rootId, rootState);
        var stump = TreeStump.TryFind(rootId, rootState, out _);
        if (stump) collected.RemoveAll(b => b.Cell == root);
        foreach (var block in collected)
            StampedTree.WriteCell(chunks, ((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z), 0, 0);

        var pivot = new Vector3(root.X + 0.5f, root.Y + (stump ? 1 : 0), root.Z + 0.5f);
        var toppling = new TopplingTree(collected, pivot, fall, drops: false, terrain: FellTerrain.Of(chunks));

        var start = new Dictionary<FellBody, float>();
        var standing = MeanBlockY(toppling);
        var worst = 0f;
        for (var frame = 0; frame < 60 * 10 && !toppling.IsDone; frame++)
        {
            toppling.Update(1f / 60f);
            foreach (var part in toppling.Parts)
            {
                if (!start.TryGetValue(part.Body, out var y))
                    start[part.Body] = y = part.Body.Center.Y;
                worst = MathF.Max(worst, part.Body.Center.Y - y);
            }
        }

        return (worst, standing - MeanBlockY(toppling), toppling.Parts.Count);
    }

    private static float MeanBlockY(TopplingTree tree)
    {
        var sum = 0f;
        var count = 0;
        foreach (var part in tree.Parts)
            for (var k = 0; k < part.Blocks.Count; k++, count++)
                sum += part.Body.PointNow(k).Y;
        return sum / count;
    }

    [Fact]
    public void ATreeFelledInADenseForestNeverRisesAndComesDown()
    {
        TestContent.EnsureLoaded();
        var failures = new List<string>();
        foreach (var species in StampedTree.WorldgenSpecies)
        foreach (var fall in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var (rise, drop, pieces) = Fell(species, fall);
            output.WriteLine($"{species.Name} toward {fall}: rose {rise:0.00}, dropped {drop:0.00}, {pieces} pieces");
            if (rise > RiseTolerance)
                failures.Add($"{species.Name} toward {fall} rose {rise:0.00}");
            if (drop < 1f)
                failures.Add($"{species.Name} toward {fall} stayed up (dropped {drop:0.00})");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
